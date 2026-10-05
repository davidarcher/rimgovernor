package main

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

var testPaths = Paths{Profile: `C:\p`, Config: `C:\c`, Game: "rimgovernor-trial", State: `C:\s.sqlite`}

func TestServeArgsDefaults(t *testing.T) {
	got, err := ServeArgs(DefaultSettings(), testPaths, 8787)
	if err != nil {
		t.Fatal(err)
	}
	want := []string{"serve", "--profile", `C:\p`, "--config", `C:\c`, "--game", "rimgovernor-trial", "--state", `C:\s.sqlite`, "--listen", "127.0.0.1:8787", "--resume"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %q\nwant %q", got, want)
	}
}

func TestServeArgsEverything(t *testing.T) {
	s := DefaultSettings()
	s.LayoutOverlay, s.ExtraArgs = false, `--routine-silver-reserve 3 --x "a b"`
	got, err := ServeArgs(s, testPaths, 9000)
	if err != nil {
		t.Fatal(err)
	}
	tail := strings.Join(got[11:], " ")
	want := "--resume --layout-overlay=false --routine-silver-reserve 3 --x a b"
	if got[10] != "127.0.0.1:9000" || tail != want {
		t.Fatalf("got %q", got)
	}
	if got[len(got)-1] != "a b" {
		t.Fatalf("quoted arg split: %q", got)
	}
}

func TestServeArgsObserveDropsPlayFlags(t *testing.T) {
	s := DefaultSettings()
	s.Observe = true
	got, err := ServeArgs(s, testPaths, 8787)
	if err != nil {
		t.Fatal(err)
	}
	joined := strings.Join(got, " ")
	if !strings.Contains(joined, "--observe") {
		t.Fatal(joined)
	}
	for _, f := range []string{"--profile", "--resume"} {
		if strings.Contains(joined, f) {
			t.Errorf("observe passes %s: %s", f, joined)
		}
	}
}

func TestValidate(t *testing.T) {
	for name, mutate := range map[string]func(*Settings){
		"quote": func(s *Settings) { s.ExtraArgs = `"open` },
	} {
		s := DefaultSettings()
		mutate(&s)
		if s.Validate() == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

func TestSettingsRoundTrip(t *testing.T) {
	path := filepath.Join(t.TempDir(), "launcher.json")
	if s, err := LoadSettings(path); err != nil || !reflect.DeepEqual(s, DefaultSettings()) {
		t.Fatalf("missing file: %+v %v", s, err)
	}
	s := DefaultSettings()
	s.LayoutOverlay = false
	if err := SaveSettings(path, s); err != nil {
		t.Fatal(err)
	}
	if got, err := LoadSettings(path); err != nil || !reflect.DeepEqual(got, s) {
		t.Fatalf("%+v %v", got, err)
	}
	// Settings saved before #875 carry the removed speed and herd settings; they load.
	if err := os.WriteFile(path, []byte(`{"speed":"Fast","allowSlaughter":true,"allowRelease":true}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if got, err := LoadSettings(path); err != nil || !reflect.DeepEqual(got, DefaultSettings()) {
		t.Fatalf("old settings: %+v %v", got, err)
	}
}

func TestStatePath(t *testing.T) {
	dir := t.TempDir()
	now := time.Date(2026, 9, 26, 10, 0, 0, 0, time.UTC)
	fresh := filepath.Join(dir, "state-20260926-100000.sqlite")
	var asked string
	var running func(string) (bool, error)
	live := func(p string) (bool, error) { asked = p; return running(p) }
	running = func(string) (bool, error) { return true, nil }
	if got, why := StatePath(dir, true, now, live); got != fresh || !strings.Contains(why, "no earlier state") {
		t.Fatalf("empty dir: %s (%s)", got, why)
	}
	for _, n := range []string{"state-20260101-000000.sqlite", "state-20260301-000000.sqlite"} {
		os.WriteFile(filepath.Join(dir, n), nil, 0644)
	}
	if got, why := StatePath(dir, true, now, live); filepath.Base(got) != "state-20260301-000000.sqlite" || filepath.Base(asked) != filepath.Base(got) || !strings.Contains(why, "continuing") {
		t.Fatalf("continue: %s (%s)", got, why)
	}
	running = func(string) (bool, error) { return false, nil }
	if got, why := StatePath(dir, true, now, live); got != fresh || !strings.Contains(why, "not running") {
		t.Fatalf("no live control: %s (%s)", got, why)
	}
	running = func(string) (bool, error) { return false, errors.New("locked") }
	if got, why := StatePath(dir, true, now, live); got != fresh || !strings.Contains(why, "locked") {
		t.Fatalf("unreadable: %s (%s)", got, why)
	}
	if got, why := StatePath(dir, false, now, live); got != fresh || !strings.Contains(why, "continue is off") {
		t.Fatalf("fresh: %s (%s)", got, why)
	}
}

func TestLiveControl(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "state.sqlite")
	s, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	s.Close()
	if ok, err := LiveControl(path); ok || err != nil {
		t.Fatalf("no record: %v %v", ok, err)
	}
	world := store.World{Colony: "colony", Load: "load"}
	step := func(id string, kind store.ControlKind, phase store.ControlPhase, g domain.NativeGeneration) {
		t.Helper()
		s, err := store.Open(ctx, path)
		if err != nil {
			t.Fatal(err)
		}
		defer s.Close()
		if _, _, err := s.BeginControl(ctx, store.ControlRequest{RequestID: id, Kind: kind, World: world}); err != nil {
			t.Fatal(err)
		}
		if _, err := s.CompleteControl(ctx, id, phase, g); err != nil {
			t.Fatal(err)
		}
	}
	step("resume1", store.ResumeControl, store.RunningControl, 3)
	if ok, err := LiveControl(path); !ok || err != nil {
		t.Fatalf("running: %v %v", ok, err)
	}
	step("pause1", store.PauseControl, store.PausedControl, 0)
	if ok, err := LiveControl(path); ok || err != nil {
		t.Fatalf("paused: %v %v", ok, err)
	}
}

func TestControlColony(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "state.sqlite")
	s, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	if c, err := ControlColony(path); c != "" || err != nil {
		t.Fatalf("no record: %q %v", c, err)
	}
	if _, _, err := s.BeginControl(ctx, store.ControlRequest{RequestID: "resume1", Kind: store.ResumeControl, World: store.World{Colony: "abc", Load: "load"}}); err != nil {
		t.Fatal(err)
	}
	if c, err := ControlColony(path); c != "abc" || err != nil {
		t.Fatalf("got %q %v", c, err)
	}
}

func TestLatestColonySave(t *testing.T) {
	dir := t.TempDir()
	write := func(name, colony string, age time.Duration) {
		t.Helper()
		p := filepath.Join(dir, name+".rws")
		if err := os.WriteFile(p, []byte("<game><rimgovernorColonyId>"+colony+"</rimgovernorColonyId></game>"), 0o644); err != nil {
			t.Fatal(err)
		}
		at := time.Now().Add(-age)
		if err := os.Chtimes(p, at, at); err != nil {
			t.Fatal(err)
		}
	}
	write("Autosave-1", "abc", 3*time.Hour)
	write("checkpoint", "abc", time.Hour)
	write("Autosave-2", "other", 0)
	if got, err := LatestColonySave(dir, "abc"); got != "checkpoint" || err != nil {
		t.Fatalf("got %q %v", got, err)
	}
	if got, _ := LatestColonySave(dir, "missing"); got != "" {
		t.Fatalf("missing colony matched %q", got)
	}
	if got, _ := LatestColonySave(dir, ""); got != "" {
		t.Fatalf("empty colony matched %q", got)
	}
}

func TestReloadSave(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	var loads []map[string]any
	fail, refuse := 1, false
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/api/player/session":
			w.Write([]byte(`{"token":"tok"}`))
		case "/api/lifecycle/load":
			var body map[string]any
			json.NewDecoder(r.Body).Decode(&body)
			loads = append(loads, body)
			switch {
			case refuse || r.Header.Get("X-RimGovernor-Player") != "tok":
				w.WriteHeader(400)
			case fail > 0:
				fail--
				w.WriteHeader(503)
			default:
				w.WriteHeader(201)
			}
		}
	}))
	defer srv.Close()
	if err := ReloadSave(context.Background(), srv.URL, "checkpoint"); err != nil {
		t.Fatal(err)
	}
	if len(loads) != 2 || loads[0]["saveName"] != "checkpoint" || loads[0]["requestId"] != loads[1]["requestId"] {
		t.Fatalf("loads %v", loads)
	}
	refuse = true
	if err := ReloadSave(context.Background(), srv.URL, "x"); err == nil {
		t.Fatal("refusal not reported")
	}
}

func TestConfiguredGame(t *testing.T) {
	if id, err := ConfiguredGame([]byte(`{"games":{"rimgovernor-trial":{}}}`)); err != nil || id != "rimgovernor-trial" {
		t.Fatal(id, err)
	}
	if _, err := ConfiguredGame([]byte(`{"games":{"a":{},"b":{}}}`)); err == nil {
		t.Fatal("two games accepted")
	}
}

func TestModState(t *testing.T) {
	for _, c := range []struct {
		m    *na.PackageManifest
		want string
	}{
		{nil, StateMissing},
		{&na.PackageManifest{SourceTree: "old"}, StateStale},
		{&na.PackageManifest{SourceTree: "h", Fixtures: []string{"DebugStartFixture"}}, StateStale},
		{&na.PackageManifest{SourceTree: "h"}, StateOK},
	} {
		if got, _ := ModState(c.m, "h"); got != c.want {
			t.Errorf("%+v: %s, want %s", c.m, got, c.want)
		}
	}
}

func TestPortOwners(t *testing.T) {
	repo := `C:\code\rimgovernor`
	stop, err := PortOwners([]Owner{{PID: 5, Path: `c:\CODE\rimgovernor\.rimgovernor\go\rimgovernor.exe`}, {PID: 7}}, repo, 7)
	if err != nil || !reflect.DeepEqual(stop, []int{5, 7}) {
		t.Fatal(stop, err)
	}
	if _, err := PortOwners([]Owner{{PID: 9, Path: `C:\code\rimgovernor-other\x.exe`}}, repo, 0); err == nil || !strings.Contains(err.Error(), "process 9") {
		t.Fatal(err)
	}
	if _, err := PortOwners([]Owner{{PID: 9, Path: `C:\code\rimgovernor\.claude\worktrees\w\.rimgovernor\go\rimgovernor.exe`}}, repo, 0); err == nil {
		t.Fatal("a nested worktree's controller is not ours")
	}
	if _, err := PortOwners([]Owner{{PID: 9}}, repo, 0); err == nil || !strings.Contains(err.Error(), "path unknown") {
		t.Fatal(err)
	}
}

// Settings saved before #875 carry the removed shrine switches; they still
// load, and the switches are ignored.
func TestLoadSettingsIgnoresRemovedShrineSwitches(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	if err := os.WriteFile(path, []byte(`{"speed":"Normal","shrineOpenCaskets":true,"shrineHeatFallback":true,"layoutOverlay":false}`), 0o644); err != nil {
		t.Fatal(err)
	}
	s, err := LoadSettings(path)
	if err != nil || s.LayoutOverlay || s.Validate() != nil {
		t.Fatal(s, err)
	}
}

func TestListSaves(t *testing.T) {
	dir := t.TempDir()
	for name, age := range map[string]time.Duration{"old": 2 * time.Hour, "new": 0, "mid": time.Hour} {
		p := filepath.Join(dir, name+".rws")
		if err := os.WriteFile(p, nil, 0o644); err != nil {
			t.Fatal(err)
		}
		at := time.Now().Add(-age)
		if err := os.Chtimes(p, at, at); err != nil {
			t.Fatal(err)
		}
	}
	os.WriteFile(filepath.Join(dir, "notes.txt"), nil, 0o644)
	if got := strings.Join(ListSaves(dir), ","); got != "new,mid,old" {
		t.Fatalf("got %q", got)
	}
	if got := ListSaves(filepath.Join(dir, "missing")); len(got) != 0 {
		t.Fatalf("missing dir listed %v", got)
	}
}
