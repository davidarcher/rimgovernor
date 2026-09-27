package main

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

var testPaths = Paths{Profile: `C:\p`, GABS: `C:\g.exe`, Config: `C:\c`, Game: "rimgovernor-trial", State: `C:\s.sqlite`, Assets: `C:\a`}

func TestServeArgsDefaults(t *testing.T) {
	got, err := ServeArgs(DefaultSettings(), testPaths, 8787)
	if err != nil {
		t.Fatal(err)
	}
	want := []string{"serve", "--profile", `C:\p`, "--gabs", `C:\g.exe`, "--config", `C:\c`, "--game", "rimgovernor-trial", "--state", `C:\s.sqlite`, "--assets", `C:\a`, "--listen", "127.0.0.1:8787"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %q\nwant %q", got, want)
	}
}

func TestServeArgsEverything(t *testing.T) {
	s := DefaultSettings()
	s.AutoStart, s.Speed, s.ChatModel, s.ChatBaseURL = true, "UltrafastAdaptive", "qwen", "http://x/v1"
	s.AllowSlaughter, s.AllowRelease, s.ShrineOpenCaskets, s.ShrineHeatFallback = true, true, true, true
	s.LayoutOverlay, s.FoodReserveDays, s.Debug, s.ExtraArgs = false, 2.5, true, `--routine-food-reserve-days 3 --x "a b"`
	got, err := ServeArgs(s, testPaths, 9000)
	if err != nil {
		t.Fatal(err)
	}
	tail := strings.Join(got[15:], " ")
	want := "--resume --clock-speed Ultrafast --chat-model qwen --chat-base-url http://x/v1 --routine-allow-slaughter --routine-allow-release --routine-shrine-open-caskets --routine-shrine-heat-fallback --layout-overlay=false --routine-food-reserve-days 2.5 --debug --routine-food-reserve-days 3 --x a b"
	if got[14] != "127.0.0.1:9000" || tail != want {
		t.Fatalf("got %q", got)
	}
	if got[len(got)-1] != "a b" {
		t.Fatalf("quoted arg split: %q", got)
	}
}

func TestServeArgsObserveDropsPlayFlags(t *testing.T) {
	s := DefaultSettings()
	s.Observe, s.AutoStart, s.Speed, s.ChatModel, s.AllowSlaughter = true, true, "Fast", "m", true
	got, err := ServeArgs(s, testPaths, 8787)
	if err != nil {
		t.Fatal(err)
	}
	joined := strings.Join(got, " ")
	if !strings.Contains(joined, "--observe") {
		t.Fatal(joined)
	}
	for _, f := range []string{"--profile", "--resume", "--clock-speed", "--chat-model", "--routine-allow-slaughter"} {
		if strings.Contains(joined, f) {
			t.Errorf("observe passes %s: %s", f, joined)
		}
	}
}

func TestValidate(t *testing.T) {
	for name, mutate := range map[string]func(*Settings){
		"speed": func(s *Settings) { s.Speed = "Warp" },
		"heat":  func(s *Settings) { s.ShrineHeatFallback = true },
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
	s.Speed = "Fast"
	if err := SaveSettings(path, s); err != nil {
		t.Fatal(err)
	}
	if got, err := LoadSettings(path); err != nil || !reflect.DeepEqual(got, s) {
		t.Fatalf("%+v %v", got, err)
	}
}

func TestStatePath(t *testing.T) {
	dir := t.TempDir()
	now := time.Date(2026, 9, 26, 10, 0, 0, 0, time.UTC)
	fresh := filepath.Join(dir, "state-20260926-100000.sqlite")
	if got := StatePath(dir, true, now); got != fresh {
		t.Fatalf("empty dir: %s", got)
	}
	for _, n := range []string{"state-20260101-000000.sqlite", "state-20260301-000000.sqlite"} {
		os.WriteFile(filepath.Join(dir, n), nil, 0644)
	}
	if got := StatePath(dir, true, now); filepath.Base(got) != "state-20260301-000000.sqlite" {
		t.Fatalf("continue: %s", got)
	}
	if got := StatePath(dir, false, now); got != fresh {
		t.Fatalf("fresh: %s", got)
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

func TestDashboardState(t *testing.T) {
	dir := t.TempDir()
	if s, _ := DashboardState(dir); s != StateMissing {
		t.Fatal(s)
	}
	os.MkdirAll(filepath.Join(dir, "dist"), 0755)
	os.MkdirAll(filepath.Join(dir, "src"), 0755)
	src := filepath.Join(dir, "src", "App.tsx")
	built := filepath.Join(dir, "dist", "index.html")
	os.WriteFile(src, nil, 0644)
	os.WriteFile(built, nil, 0644)
	old, fresh := time.Now().Add(-time.Hour), time.Now()
	os.Chtimes(src, old, old)
	os.Chtimes(built, fresh, fresh)
	if s, _ := DashboardState(dir); s != StateOK {
		t.Fatal(s)
	}
	os.Chtimes(src, fresh.Add(time.Minute), fresh.Add(time.Minute))
	if s, why := DashboardState(dir); s != StateStale || why != "src/App.tsx is newer than the build" {
		t.Fatal(s, why)
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

func TestUnpackGABSRefusesWrongHash(t *testing.T) {
	if err := unpackGABS([]byte("not the release"), t.TempDir()); err == nil || !strings.Contains(err.Error(), "refusing") {
		t.Fatal(err)
	}
}
