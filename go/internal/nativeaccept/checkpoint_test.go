package nativeaccept

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestOffsetLabel(t *testing.T) {
	t.Parallel()
	for d, want := range map[time.Duration]string{
		0: "t+0s", 10 * time.Second: "t+10s", 61 * time.Second: "t+1m1s", 7 * time.Minute: "t+7m",
		90 * time.Second: "t+1m30s", time.Hour: "t+1h", 65 * time.Minute: "t+1h5m", 5 * time.Millisecond: "t+5ms",
		1003 * time.Millisecond: "t+1.003s",
	} {
		if got := OffsetLabel(d); got != want {
			t.Errorf("OffsetLabel(%s) = %s, want %s", d, got, want)
		}
	}
}

func TestFingerprintMismatch(t *testing.T) {
	t.Parallel()
	f := Fingerprint{Package: "p", Start: "s", Expansions: []string{"ludeon.rimworld.royalty"}}
	same := Checkpoint{Package: "p", Start: "s", Expansions: []string{"ludeon.rimworld.royalty"}}
	if got := f.Mismatch(same); got != "" {
		t.Fatalf("same fingerprint mismatches: %s", got)
	}
	for name, c := range map[string]Checkpoint{
		"package":    {Package: "q", Start: "s", Expansions: same.Expansions},
		"start":      {Package: "p", Start: "t", Expansions: same.Expansions},
		"expansions": {Package: "p", Start: "s"},
	} {
		if got := f.Mismatch(c); !strings.Contains(strings.ToLower(got), name) {
			t.Errorf("%s change: mismatch %q", name, got)
		}
	}
}

// Ring.Plan: a run from scratch resumes from its last entry; a resumed
// run that fails at the previous attempt's tick steps back one entry and,
// past the ring, starts fresh; a resumed run that got further resumes from
// its own last entry.
func TestRingPlan(t *testing.T) {
	t.Parallel()
	entries := []Checkpoint{{Label: "t+5m", OffsetMs: 300000}, {Label: "t+6m", OffsetMs: 360000}, {Label: "t+7m", OffsetMs: 420000}}
	first := &Ring{Entries: entries}
	first.Plan(nil, "", 9000)
	if first.Next != "t+7m" || first.Note != "" {
		t.Fatalf("from scratch: next=%s note=%q", first.Next, first.Note)
	}
	first.FailedTick = 9000

	stuck := &Ring{Entries: entries}
	stuck.Plan(first, "t+7m", 9000)
	if stuck.Next != "t+6m" || !strings.Contains(stuck.Note, "no progress since t+7m; rewinding to t+6m") {
		t.Fatalf("no progress: next=%s note=%q", stuck.Next, stuck.Note)
	}
	stuck.FailedTick = 9000

	progressed := &Ring{Entries: append(entries, Checkpoint{Label: "t+8m", OffsetMs: 480000})}
	progressed.Plan(stuck, "t+6m", 12000)
	if progressed.Next != "t+8m" || progressed.Note != "" {
		t.Fatalf("progress: next=%s note=%q", progressed.Next, progressed.Note)
	}

	exhausted := &Ring{Entries: entries[:1]}
	exhausted.Plan(&Ring{FailedTick: 9000}, "t+5m", 9000)
	if exhausted.Next != "" || !strings.Contains(exhausted.Note, "starting fresh") {
		t.Fatalf("past the ring: next=%q note=%q", exhausted.Next, exhausted.Note)
	}

	unobserved := &Ring{Entries: entries}
	unobserved.Plan(first, "t+7m", 0)
	if unobserved.Next != "t+7m" {
		t.Fatalf("an unobserved failure tick must not rewind: next=%s", unobserved.Next)
	}
}

func TestRingRewindAndRoundTrip(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	r := &Ring{Case: "a/b", Next: "t+3m", Entries: []Checkpoint{{Label: "t+1m"}, {Label: "t+2m"}, {Label: "t+3m"}}, Failed: &Checkpoint{Label: FailedCheckpoint}}
	if got := r.Rewind(0); got != "t+3m" {
		t.Fatal(got)
	}
	if got := r.Rewind(2); got != "t+1m" {
		t.Fatal(got)
	}
	if got := r.Rewind(3); got != "" {
		t.Fatalf("past the ring: %q", got)
	}
	if err := r.Write(dir); err != nil {
		t.Fatal(err)
	}
	back, err := ReadRing(dir)
	if err != nil {
		t.Fatal(err)
	}
	if back.Next != "t+3m" || len(back.Entries) != 3 || back.Entries[2].Path != filepath.Join(dir, "t+3m") || back.Failed.Path != filepath.Join(dir, FailedCheckpoint) {
		t.Fatalf("%+v", back)
	}
	if none, err := ReadRing(filepath.Join(dir, "missing")); none != nil || err != nil {
		t.Fatal(none, err)
	}
}

// ringFixture is a root with a prepared configuration (a profile with a
// clock journal), a service store and a ring whose Saver writes a fake
// save into the profile.
func ringFixture(t *testing.T, every time.Duration) (*CheckpointRing, string) {
	t.Helper()
	root := t.TempDir()
	profile := filepath.Join(root, "headless-profile")
	configDir := filepath.Join(root, "config-headless")
	if err := os.MkdirAll(filepath.Join(profile, "Saves"), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(profile, "RimGovernorClockEvents"), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(profile, "RimGovernorClockEvents", "000001.xml"), []byte("<row/>"), 0644); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(configDir, 0755); err != nil {
		t.Fatal(err)
	}
	config := map[string]any{"games": map[string]any{"rimgovernor-trial": map[string]any{"args": []any{"-batchmode", "-savedatafolder=" + profile}}}}
	data, _ := json.Marshal(config)
	if err := os.WriteFile(filepath.Join(configDir, "config.json"), data, 0644); err != nil {
		t.Fatal(err)
	}
	output := filepath.Join(root, "out")
	if err := os.MkdirAll(output, 0755); err != nil {
		t.Fatal(err)
	}
	statePath := filepath.Join(output, "service.sqlite")
	s, err := store.Open(context.Background(), statePath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	cfg := &Config{Root: root, Output: output, Headless: true, Configuration: configDir}
	ring := &CheckpointRing{
		Dir: filepath.Join(root, "checkpoints", "a", "b"), Case: "a/b", Every: every, Keep: 3,
		Config: cfg, StorePath: statePath, Fingerprint: Fingerprint{Package: "p", Start: "s"},
		Saver: func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error) {
			path := cfg.profileSave(name)
			if err := os.WriteFile(path, []byte("save "+label), 0644); err != nil {
				return "", 0, nil, err
			}
			return path, 4242, map[string]any{"colonyId": "c"}, nil
		},
	}
	return ring, root
}

// A capture bundles the save, the store and the journal with a sidecar;
// the ring keeps its last Keep entries and prunes the rest from disk.
func TestCheckpointRingCapturesAndPrunes(t *testing.T) {
	ring, _ := ringFixture(t, time.Millisecond)
	ring.Activate()
	defer ring.Deactivate()
	ctx := context.Background()
	var labels []string
	for i := 0; i < 5; i++ {
		time.Sleep(2 * time.Millisecond)
		if took := checkpointPause(ctx); took == 0 {
			t.Fatalf("capture %d did not run", i)
		}
		entries := ring.Entries()
		labels = append(labels, entries[len(entries)-1].Label)
	}
	if errs := ring.Errors(); len(errs) > 0 {
		t.Fatal(errs)
	}
	entries := ring.Entries()
	if len(entries) != 3 {
		t.Fatalf("kept %d entries, want 3: %v", len(entries), labels)
	}
	last := entries[2]
	if !last.Store || !last.Journal || last.Tick != 4242 || last.Through != "saver" || last.Save != CheckpointSaveName("a/b") {
		t.Fatalf("%+v", last)
	}
	for _, name := range []string{last.Save + ".rws", CheckpointStoreFile, CheckpointSidecar, filepath.Join(CheckpointJournalDir, "000001.xml")} {
		if _, err := os.Stat(filepath.Join(last.Path, name)); err != nil {
			t.Errorf("bundle lacks %s: %v", name, err)
		}
	}
	var sidecar Checkpoint
	data, _ := os.ReadFile(filepath.Join(last.Path, CheckpointSidecar))
	if err := json.Unmarshal(data, &sidecar); err != nil || sidecar.Label != last.Label || sidecar.Package != "p" {
		t.Fatalf("sidecar %+v %v", sidecar, err)
	}
	dirs, _ := os.ReadDir(ring.Dir)
	bundles := 0
	for _, d := range dirs {
		if d.IsDir() {
			bundles++
		}
	}
	if bundles != 3 {
		t.Fatalf("ring directory holds %d bundles, want 3", bundles)
	}
	// Each capture leaves a provisional index naming itself as next.
	provisional, err := ReadRing(ring.Dir)
	if err != nil || provisional == nil || provisional.Next != last.Label || len(provisional.Entries) != 3 || provisional.Note == "" {
		t.Fatalf("provisional index %+v %v", provisional, err)
	}
	// Labels are distinct even at a sub-second cadence.
	seen := map[string]bool{}
	for _, l := range labels {
		if seen[l] {
			t.Fatalf("label %s repeated: %v", l, labels)
		}
		seen[l] = true
	}
}

// A sub-second cadence past the one-second mark still labels every
// capture distinctly, so no capture overwrites a live bundle and the
// pruned ring matches its directory (#596).
func TestCheckpointRingLabelsPastOneSecond(t *testing.T) {
	ring, _ := ringFixture(t, time.Millisecond)
	ring.Base = time.Second
	ring.Activate()
	defer ring.Deactivate()
	ctx := context.Background()
	for i := 0; i < 5; i++ {
		time.Sleep(2 * time.Millisecond)
		if took := checkpointPause(ctx); took == 0 {
			t.Fatalf("capture %d did not run", i)
		}
	}
	entries := ring.Entries()
	dirs, _ := os.ReadDir(ring.Dir)
	bundles := 0
	for _, d := range dirs {
		if d.IsDir() {
			bundles++
		}
	}
	if len(entries) != 3 || bundles != 3 {
		t.Fatalf("kept %d entries over %d bundles, want 3 and 3: %+v", len(entries), bundles, entries)
	}
}

// A running game declines a periodic capture; the ring backs off instead
// of asking on every call, and a forced capture (Fail) still takes it.
func TestCheckpointRingBacksOffWhileRunning(t *testing.T) {
	ring, _ := ringFixture(t, time.Millisecond)
	saver := ring.Saver
	forced := 0
	ring.Saver = func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error) {
		if !force {
			return "", 0, nil, errNotPaused
		}
		forced++
		return saver(ctx, name, label, force)
	}
	ring.Activate()
	defer ring.Deactivate()
	time.Sleep(2 * time.Millisecond)
	ctx := context.Background()
	checkpointPause(ctx)
	if len(ring.Entries()) != 0 {
		t.Fatal("captured while running")
	}
	ring.mu.Lock()
	backoff := ring.nextCheck
	ring.mu.Unlock()
	if !backoff.After(time.Now()) {
		t.Fatal("no back-off after a declined capture")
	}
	if len(ring.Errors()) != 0 {
		t.Fatalf("a declined capture is not an error: %v", ring.Errors())
	}
	if failed := ring.Fail(ctx); failed == nil || failed.Label != FailedCheckpoint || forced != 1 {
		t.Fatalf("failed bundle %+v forced=%d errors=%v", failed, forced, ring.Errors())
	}
}

// Capture errors are recorded, not raised, and the ring tries again at
// the next cadence.
func TestCheckpointRingRecordsCaptureErrors(t *testing.T) {
	ring, _ := ringFixture(t, time.Millisecond)
	ring.Saver = func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error) {
		return "", 0, nil, errors.New("save refused")
	}
	ring.Activate()
	defer ring.Deactivate()
	time.Sleep(2 * time.Millisecond)
	checkpointPause(context.Background())
	if errs := ring.Errors(); len(errs) != 1 || !strings.Contains(errs[0], "save refused") {
		t.Fatal(errs)
	}
	if dirs, _ := os.ReadDir(ring.Dir); len(dirs) != 0 {
		t.Fatal("a failed capture left a bundle behind")
	}
}

// Staging a bundle puts its save in profile/Saves (replacing an older
// copy) and its store where the service opens it; restoring its journal
// replaces the profile's.
func TestStageCheckpointAndRestoreJournal(t *testing.T) {
	ring, root := ringFixture(t, time.Millisecond)
	ring.Activate()
	time.Sleep(2 * time.Millisecond)
	checkpointPause(context.Background())
	ring.Deactivate()
	entries := ring.Entries()
	if len(entries) != 1 {
		t.Fatal(ring.Errors())
	}
	entry := entries[0]
	stale := filepath.Join(root, "profile", "Saves", entry.Save+".rws")
	if err := os.MkdirAll(filepath.Dir(stale), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(stale, []byte("stale"), 0644); err != nil {
		t.Fatal(err)
	}
	storePath := filepath.Join(root, "out2", "service.sqlite")
	name, err := StageCheckpoint(root, entry, storePath)
	if err != nil {
		t.Fatal(err)
	}
	if name != entry.Save {
		t.Fatal(name)
	}
	if data, _ := os.ReadFile(stale); string(data) != "save "+entry.Label {
		t.Fatalf("staged save = %q", data)
	}
	if _, err := os.Stat(storePath); err != nil {
		t.Fatal(err)
	}
	journal := filepath.Join(root, "headless-profile", "RimGovernorClockEvents")
	if err := os.WriteFile(filepath.Join(journal, "000002.xml"), []byte("<later/>"), 0644); err != nil {
		t.Fatal(err)
	}
	if err := RestoreClockJournal(ring.Config.Configuration, entry.Path); err != nil {
		t.Fatal(err)
	}
	rows, _ := os.ReadDir(journal)
	if len(rows) != 1 || rows[0].Name() != "000001.xml" {
		t.Fatalf("restored journal: %v", rows)
	}
}

// ServiceSave pauses to manual control, saves and resumes, holding the
// keep-alive throughout.
func TestServiceSaveSequence(t *testing.T) {
	t.Parallel()
	root := t.TempDir()
	if err := os.MkdirAll(filepath.Join(root, "headless-profile", "Saves"), 0755); err != nil {
		t.Fatal(err)
	}
	held := false
	var calls []string
	mode := "automate"
	api := func(method, path string, body map[string]any, token string) (map[string]any, int, error) {
		calls = append(calls, method+" "+path)
		if !held {
			t.Fatal("keep-alive not held during", path)
		}
		switch path {
		case "/api/player/control/pause":
			mode = "manual"
			return map[string]any{}, 200, nil
		case "/api/state":
			return map[string]any{"mode": mode}, 200, nil
		case "/api/lifecycle/save":
			if mode != "manual" || token != "tok" || body["saveName"] != "ring" {
				t.Fatal(mode, token, body)
			}
			if err := os.WriteFile(filepath.Join(root, "headless-profile", "Saves", "ring.rws"), []byte("save"), 0644); err != nil {
				t.Fatal(err)
			}
			return map[string]any{}, 201, nil
		case "/api/player/control/resume":
			mode = "automate"
			return map[string]any{}, 200, nil
		}
		t.Fatal("unexpected call", path)
		return nil, 0, nil
	}
	cfg := &Config{Root: root, Headless: true}
	path, err := ServiceSave(context.Background(), cfg, "ring", func(h bool) { held = h }, api, map[string]any{"colonyId": "c"}, "tok", "test")
	if err != nil {
		t.Fatal(err)
	}
	if path != filepath.Join(root, "headless-profile", "Saves", "ring.rws") || held {
		t.Fatal(path, held)
	}
	if want := "POST /api/player/control/pause,GET /api/state,POST /api/lifecycle/save,POST /api/player/control/resume"; strings.Join(calls, ",") != want {
		t.Fatal(calls)
	}
}

func TestServiceSaveResumesAfterFailedSave(t *testing.T) {
	t.Parallel()
	var calls []string
	mode := "automate"
	api := func(method, path string, body map[string]any, token string) (map[string]any, int, error) {
		calls = append(calls, method+" "+path)
		switch path {
		case "/api/player/control/pause":
			mode = "manual"
			return map[string]any{}, 200, nil
		case "/api/state":
			return map[string]any{"mode": mode}, 200, nil
		case "/api/lifecycle/save":
			return map[string]any{"code": "unavailable"}, 503, nil
		case "/api/player/control/resume":
			mode = "automate"
			return map[string]any{}, 200, nil
		}
		t.Fatal("unexpected call", path)
		return nil, 0, nil
	}
	cfg := &Config{Root: t.TempDir(), Headless: true}
	_, err := ServiceSave(context.Background(), cfg, "ring", func(bool) {}, api, nil, "tok", "test")
	if err == nil || !strings.Contains(err.Error(), "save status=503") {
		t.Fatal(err)
	}
	if mode != "automate" || calls[len(calls)-1] != "POST /api/player/control/resume" {
		t.Fatal(mode, calls)
	}
}

// The case's own progress record (SetCheckpointState) rides every later
// capture's sidecar, so a resumed run can skip the prep the save carries
// (#316); it is a no-op with no ring active.
func TestCheckpointStateRidesCaptures(t *testing.T) {
	SetCheckpointState("orphan", 1)
	ring, _ := ringFixture(t, time.Hour)
	ring.Activate()
	defer ring.Deactivate()
	ctx := context.Background()
	first, err := ring.Capture(ctx, "before")
	if err != nil {
		t.Fatal(err)
	}
	if first.State != nil {
		t.Fatalf("state before any record: %v", first.State)
	}
	SetCheckpointState("fixture", map[string]any{"siteX": 7})
	SetCheckpointState("gone", true)
	SetCheckpointState("gone", nil)
	second, err := ring.Capture(ctx, "after")
	if err != nil {
		t.Fatal(err)
	}
	site, _ := second.State["fixture"].(map[string]any)
	if _, gone := second.State["gone"]; gone || site["siteX"] != 7 {
		t.Fatalf("state %v", second.State)
	}
	var sidecar Checkpoint
	data, _ := os.ReadFile(filepath.Join(second.Path, CheckpointSidecar))
	if err := json.Unmarshal(data, &sidecar); err != nil {
		t.Fatal(err)
	}
	if got, _ := sidecar.State["fixture"].(map[string]any); got["siteX"] != float64(7) {
		t.Fatalf("sidecar state %v", sidecar.State)
	}
}

// A capped ring takes no further entry, periodic or named, but still
// takes the failed bundle, so a resume replays from before the cap (#330).
func TestCapCheckpointsStopsEntries(t *testing.T) {
	CapCheckpoints("orphan")
	ring, _ := ringFixture(t, time.Nanosecond)
	ring.Activate()
	defer ring.Deactivate()
	ctx := context.Background()
	if _, err := ring.Capture(ctx, "before"); err != nil {
		t.Fatal(err)
	}
	CapCheckpoints("raid staged")
	CapCheckpoints("later reason")
	if got := ring.Capped(); got != "raid staged" {
		t.Fatalf("capped %q", got)
	}
	if _, err := ring.Capture(ctx, "named"); err == nil {
		t.Fatal("named capture after the cap succeeded")
	}
	ring.mu.Lock()
	ring.last = time.Time{}
	ring.mu.Unlock()
	if took := ring.maybe(ctx); took != 0 {
		t.Fatalf("periodic capture ran after the cap (%s)", took)
	}
	if failed := ring.Fail(ctx); failed == nil {
		t.Fatal("failed bundle not taken after the cap")
	}
	labels := []string{}
	for _, e := range ring.Entries() {
		labels = append(labels, e.Label)
	}
	if len(labels) != 2 || labels[0] != "before" || labels[1] != FailedCheckpoint {
		t.Fatalf("entries %v", labels)
	}
}

// A periodic service capture waits until the service automates with a
// fresh game read; a state without a known tick would 503 the save (#309).
func TestServiceCanCapture(t *testing.T) {
	t.Parallel()
	game := func(stale bool, tick any) map[string]any {
		return map[string]any{"stale": stale, "tick": tick}
	}
	for name, tc := range map[string]struct {
		state map[string]any
		want  bool
	}{
		"automating with a tick": {map[string]any{"mode": "automate", "game": game(false, 120.0)}, true},
		"manual":                 {map[string]any{"mode": "manual", "game": game(false, 120.0)}, false},
		"stale read":             {map[string]any{"mode": "automate", "game": game(true, 120.0)}, false},
		"no tick yet":            {map[string]any{"mode": "automate", "game": game(false, nil)}, false},
		"no game":                {map[string]any{"mode": "automate"}, false},
	} {
		if got := serviceCanCapture(tc.state); got != tc.want {
			t.Errorf("%s: serviceCanCapture=%v want %v", name, got, tc.want)
		}
	}
}
