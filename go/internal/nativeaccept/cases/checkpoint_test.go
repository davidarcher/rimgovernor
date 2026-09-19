package cases

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// ringRoot is a prepared root: a headless configuration whose game
// installation carries the native package, so fingerprint() works.
func ringRoot(t *testing.T) Options {
	t.Helper()
	root := t.TempDir()
	game := filepath.Join(root, "game")
	pkg := filepath.Join(game, "Mods", "RimGovernor")
	for name, body := range map[string]string{
		filepath.Join("About", "About.xml"):                                   "<ModMetaData><packageId>" + na.NativePackage + "</packageId></ModMetaData>",
		filepath.Join("Assemblies", "RimGovernor.Runtime.dll"):                "runtime",
		filepath.Join("BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"): "bridge",
	} {
		path := filepath.Join(pkg, name)
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(body), 0644); err != nil {
			t.Fatal(err)
		}
	}
	configDir := filepath.Join(root, "config-headless")
	if err := os.MkdirAll(configDir, 0755); err != nil {
		t.Fatal(err)
	}
	config := map[string]any{"games": map[string]any{"rimgovernor-trial": map[string]any{
		"workingDir": game, "args": []any{"-savedatafolder=" + filepath.Join(root, "headless-profile")},
	}}}
	data, _ := json.Marshal(config)
	if err := os.WriteFile(filepath.Join(configDir, "config.json"), data, 0644); err != nil {
		t.Fatal(err)
	}
	return Options{Root: root, Headless: true, CheckpointEvery: time.Minute}
}

var ringCase = Case{Name: "a/b", Start: Save{Name: "seed"}}

// seedRing writes a ring of labelled bundles (each with a sidecar, stamped
// with the current fingerprint) whose last run failed at failedTick.
func seedRing(t *testing.T, opts Options, labels []string, next string, failedTick uint64) *na.Ring {
	t.Helper()
	fp, err := fingerprint(ringCase, opts.configDir(ringCase))
	if err != nil {
		t.Fatal(err)
	}
	dir := opts.RingDir(ringCase)
	ring := &na.Ring{Case: ringCase.Name, Next: next, FailedTick: failedTick, FailedOffsetMs: 480000, SourceRevision: "abc123"}
	for i, label := range labels {
		entry := na.Checkpoint{Case: ringCase.Name, Label: label, OffsetMs: int64(i+1) * 60000, Tick: uint64(i+1) * 1000, Save: na.CheckpointSaveName(ringCase.Name), Package: fp.Package, Start: fp.Start, Expansions: fp.Expansions, Path: filepath.Join(dir, label)}
		if err := os.MkdirAll(entry.Path, 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(entry.Path, na.CheckpointSidecar), []byte("{}"), 0644); err != nil {
			t.Fatal(err)
		}
		ring.Entries = append(ring.Entries, entry)
	}
	if err := ring.Write(dir); err != nil {
		t.Fatal(err)
	}
	return ring
}

func TestPlanResumeFromLastEntry(t *testing.T) {
	opts := ringRoot(t)
	seedRing(t, opts, []string{"t+6m", "t+7m"}, "t+7m", 9000)
	var log bytes.Buffer
	r, err := planResume(ringCase, opts, &log)
	if err != nil {
		t.Fatal(err)
	}
	if !r.resuming() || r.entry.Label != "t+7m" || r.entry.Path != filepath.Join(opts.RingDir(ringCase), "t+7m") {
		t.Fatalf("%+v", r)
	}
	if want := "resuming a/b from t+7m (rev abc123, failed at t+8m); -fresh starts over\n"; log.String() != want {
		t.Fatalf("log %q", log.String())
	}
}

func TestPlanResumeSwitchesOff(t *testing.T) {
	opts := ringRoot(t)
	seedRing(t, opts, []string{"t+7m"}, "t+7m", 9000)
	for name, o := range map[string]Options{
		"cadence off":  {Root: opts.Root, Headless: true},
		"rewound past": {Root: opts.Root, Headless: true, CheckpointEvery: time.Minute, Rewind: 1},
	} {
		var log bytes.Buffer
		r, err := planResume(ringCase, o, &log)
		if err != nil || r.resuming() {
			t.Fatalf("%s: %+v %v", name, r, err)
		}
	}
	excluded := Case{Name: "speedmatrix/x", Start: Save{Name: "seed"}}
	if r, _ := planResume(excluded, opts, &bytes.Buffer{}); r.resuming() {
		t.Fatal("a speedmatrix case resumed")
	}
	fresh := opts
	fresh.Fresh = true
	if r, err := planResume(ringCase, fresh, &bytes.Buffer{}); err != nil || r.resuming() {
		t.Fatal(r, err)
	}
	if _, err := os.Stat(opts.RingDir(ringCase)); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("-fresh left the ring behind")
	}
}

func TestPlanResumeRewindsAndNotes(t *testing.T) {
	opts := ringRoot(t)
	ring := seedRing(t, opts, []string{"t+5m", "t+6m", "t+7m"}, "t+7m", 9000)
	ring.Note = "no progress since t+7m; rewinding to t+6m"
	ring.Next = "t+6m"
	if err := ring.Write(opts.RingDir(ringCase)); err != nil {
		t.Fatal(err)
	}
	opts.Rewind = 1
	var log bytes.Buffer
	r, err := planResume(ringCase, opts, &log)
	if err != nil || r.entry.Label != "t+5m" {
		t.Fatalf("%+v %v", r, err)
	}
	for _, want := range []string{"no progress since t+7m; rewinding to t+6m\n", "-rewind 1: t+5m instead of t+6m\n", "resuming a/b from t+5m"} {
		if !strings.Contains(log.String(), want) {
			t.Fatalf("log %q lacks %q", log.String(), want)
		}
	}
}

// A changed native package or Start invalidates the ring with a printed
// reason.
func TestPlanResumeDiscardsStaleRing(t *testing.T) {
	opts := ringRoot(t)
	seedRing(t, opts, []string{"t+7m"}, "t+7m", 9000)
	dll := filepath.Join(opts.Root, "game", "Mods", "RimGovernor", "Assemblies", "RimGovernor.Runtime.dll")
	if err := os.WriteFile(dll, []byte("rebuilt"), 0644); err != nil {
		t.Fatal(err)
	}
	var log bytes.Buffer
	r, err := planResume(ringCase, opts, &log)
	if err != nil || r.resuming() {
		t.Fatal(r, err)
	}
	if want := "discarding checkpoint ring of a/b: the installed native package changed; starting fresh\n"; log.String() != want {
		t.Fatalf("log %q", log.String())
	}
	if _, err := os.Stat(opts.RingDir(ringCase)); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the stale ring was kept")
	}
	seedRing(t, opts, []string{"t+7m"}, "t+7m", 9000)
	moved := ringCase
	moved.Start = Save{Name: "other"}
	log.Reset()
	if r, _ := planResume(moved, opts, &log); r.resuming() || !strings.Contains(log.String(), "the case's Start changed") {
		t.Fatal(r, log.String())
	}
}

func testRing(opts Options, base time.Duration, saver func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error)) *na.CheckpointRing {
	return &na.CheckpointRing{
		Dir: opts.RingDir(ringCase), Case: ringCase.Name, Every: time.Minute, Keep: na.CheckpointKeep, Base: base,
		Config: &na.Config{Root: opts.Root, Headless: true, Configuration: opts.configDir(ringCase)},
		Saver:  saver, SourceRevision: "def456",
	}
}

// A failing run keeps the ring: the entries up to the resume point plus its
// own, the failed bundle and Plan's next entry; a run stuck at the same
// tick rewinds.
func TestCloseRingKeepsTimelineAndAutoRewinds(t *testing.T) {
	opts := ringRoot(t)
	previous := seedRing(t, opts, []string{"t+5m", "t+6m", "t+7m"}, "t+7m", 9000)
	resumed := resumption{entry: previous.Entries[2], previous: previous}
	ring := testRing(opts, 7*time.Minute, func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error) {
		path := filepath.Join(opts.Root, "headless-profile", "Saves", name+".rws")
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			return "", 0, nil, err
		}
		return path, 9000, nil, os.WriteFile(path, []byte(label), 0644)
	})
	report := na.Report{}
	closeRing(ring, resumed, errors.New("stalled"), report, nil)
	rows, _ := report["checkpoints"].([]map[string]any)
	if len(rows) != 1 || rows[0]["label"] != na.FailedCheckpoint {
		t.Fatalf("checkpoints %v errors %v", rows, report["checkpoint_errors"])
	}
	next, err := na.ReadRing(ring.Dir)
	if err != nil || next == nil {
		t.Fatal(next, err)
	}
	var labels []string
	for _, e := range next.Entries {
		labels = append(labels, e.Label)
	}
	if strings.Join(labels, ",") != "t+5m,t+6m,t+7m" || next.Next != "t+6m" || next.FailedTick != 9000 || next.SourceRevision != "def456" {
		t.Fatalf("%+v", next)
	}
	if report["checkpoint_next"] != "t+6m" || !strings.Contains(na.AsString(report["checkpoint_note"]), "no progress since t+7m; rewinding to t+6m") {
		t.Fatalf("%v %v", report["checkpoint_next"], report["checkpoint_note"])
	}
	if next.Failed == nil || next.Failed.Path != filepath.Join(ring.Dir, na.FailedCheckpoint) {
		t.Fatalf("failed bundle %+v", next.Failed)
	}
}

// A run that resumed from the middle of the ring drops the entries after
// its resume point (they belong to the attempt that failed).
func TestCloseRingDropsEntriesPastResumePoint(t *testing.T) {
	opts := ringRoot(t)
	previous := seedRing(t, opts, []string{"t+5m", "t+6m", "t+7m"}, "t+6m", 9000)
	resumed := resumption{entry: previous.Entries[1], previous: previous}
	ring := testRing(opts, 6*time.Minute, func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error) {
		return "", 0, nil, errors.New("no game")
	})
	report := na.Report{}
	closeRing(ring, resumed, errors.New("stalled"), report, nil)
	next, err := na.ReadRing(ring.Dir)
	if err != nil || next == nil || len(next.Entries) != 2 || next.Entries[1].Label != "t+6m" {
		t.Fatalf("%+v %v", next, err)
	}
	if _, err := os.Stat(filepath.Join(ring.Dir, "t+7m")); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("t+7m bundle kept")
	}
	// The failed bundle could not be taken: no tick to compare, so the next
	// run retries t+6m rather than rewinding.
	if next.Next != "t+6m" || next.Failed != nil || len(report["checkpoint_errors"].([]string)) != 1 {
		t.Fatalf("%+v %v", next, report["checkpoint_errors"])
	}
}

func TestCloseRingClearsOnPass(t *testing.T) {
	opts := ringRoot(t)
	seedRing(t, opts, []string{"t+7m"}, "t+7m", 9000)
	ring := testRing(opts, 0, nil)
	report := na.Report{}
	closeRing(ring, resumption{}, nil, report, nil)
	if _, err := os.Stat(ring.Dir); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("a passing run kept the ring")
	}
	if rows, _ := report["checkpoints"].([]map[string]any); len(rows) != 0 {
		t.Fatal(rows)
	}
}

func TestRewindRingStepsBack(t *testing.T) {
	opts := ringRoot(t)
	previous := seedRing(t, opts, []string{"t+6m", "t+7m"}, "t+7m", 9000)
	report := na.Report{}
	rewindRing(opts.RingDir(ringCase), resumption{entry: previous.Entries[1], previous: previous}, errors.New("boom"), report)
	next, err := na.ReadRing(opts.RingDir(ringCase))
	if err != nil || next.Next != "t+6m" || !strings.Contains(next.Note, "t+7m did not open (boom); rewinding to t+6m") {
		t.Fatalf("%+v %v", next, err)
	}
	rewindRing(opts.RingDir(ringCase), resumption{entry: previous.Entries[0], previous: next}, errors.New("boom"), report)
	next, _ = na.ReadRing(opts.RingDir(ringCase))
	if next.Next != "" || !strings.Contains(next.Note, "starting fresh") {
		t.Fatalf("%+v", next)
	}
}
