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

// stagedCase declares two stages on a registered area (its sources key the
// bundles), unlike ringCase's made-up area.
var stagedCase = Case{Name: "smoke/staged", Start: Save{Name: "seed"}, Stages: []string{"shell", "ring"}}

// seedStage writes a stage bundle of stagedCase named name under the
// current fingerprint and key (or the given key).
func seedStage(t *testing.T, opts Options, name, key string, offset time.Duration) na.Checkpoint {
	t.Helper()
	fp, err := fingerprint(stagedCase, opts.configDir(stagedCase))
	if err != nil {
		t.Fatal(err)
	}
	if key == "" {
		if key, err = StageKey(stagedCase); err != nil {
			t.Fatal(err)
		}
	}
	dir := filepath.Join(opts.StagesDir(stagedCase), name)
	entry := na.Checkpoint{Case: stagedCase.Name, Label: name, Stage: name, StageKey: key, OffsetMs: offset.Milliseconds(), Tick: 5000, Save: na.CheckpointSaveName(stagedCase.Name),
		Package: fp.Package, Start: fp.Start, Expansions: fp.Expansions, Prepared: map[string]any{"site": "x"}, At: "2026-09-19T00:00:00Z", Path: dir}
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, na.CheckpointSaveName(stagedCase.Name)+".rws"), []byte(name), 0644); err != nil {
		t.Fatal(err)
	}
	data, err := json.MarshalIndent(entry, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, na.CheckpointSidecar), data, 0644); err != nil {
		t.Fatal(err)
	}
	return entry
}

func TestStageKeyFollowsAreaSourcesAndStages(t *testing.T) {
	a, err := StageKey(stagedCase)
	if err != nil || len(a) != 64 {
		t.Fatal(a, err)
	}
	b, _ := StageKey(stagedCase)
	if a != b {
		t.Fatal("the key is not stable")
	}
	reordered := stagedCase
	reordered.Stages = []string{"ring", "shell"}
	if c, _ := StageKey(reordered); c != a {
		t.Fatal("the key depends on the stages' order, not their set")
	}
	renamed := stagedCase
	renamed.Stages = []string{"shell", "roof"}
	if c, _ := StageKey(renamed); c == a {
		t.Fatal("a renamed stage kept the key")
	}
	other := stagedCase
	other.Name = "lifecycle/staged"
	if c, _ := StageKey(other); c == a {
		t.Fatal("another area's sources gave the same key")
	}
	if _, err := StageKey(Case{Name: "nosucharea/x"}); err == nil {
		t.Fatal("an area without sources keyed")
	}
}

func TestPlanStagePicksNewestMatchingStage(t *testing.T) {
	opts := ringRoot(t)
	var log bytes.Buffer
	if plan, err := planStage(stagedCase, opts, resumption{}, &log); err != nil || plan.staged() || plan.key == "" {
		t.Fatalf("%+v %v", plan, err)
	}
	seedStage(t, opts, "shell", "", 3*time.Minute)
	seedStage(t, opts, "ring", "", 6*time.Minute)
	plan, err := planStage(stagedCase, opts, resumption{}, &log)
	if err != nil || plan.hit != 1 || plan.entry.Stage != "ring" || plan.entry.OffsetMs != 360000 {
		t.Fatalf("%+v %v", plan, err)
	}
	if !strings.Contains(log.String(), "opening smoke/staged on stage ring") {
		t.Fatal(log.String())
	}
	// A pending ring resume wins over a stage; the stages its bundle
	// records as done are skipped.
	if plan, _ := planStage(stagedCase, opts, resumption{entry: na.Checkpoint{Label: "t+7m"}}, &log); plan.staged() {
		t.Fatal("a stage beat a pending resume")
	}
	if plan, _ := planStage(stagedCase, opts, resumption{entry: na.Checkpoint{Label: "t+7m", State: map[string]any{StageStateKey: "shell"}}}, &log); plan.hit != 0 {
		t.Fatalf("%+v", plan)
	}
	// A later stage under another key is discarded and the earlier one
	// still opens.
	seedStage(t, opts, "ring", "stale", 6*time.Minute)
	log.Reset()
	plan, _ = planStage(stagedCase, opts, resumption{}, &log)
	if plan.hit != 0 || !strings.Contains(log.String(), "discarding stage ring of smoke/staged: the case's staging code changed") {
		t.Fatalf("%+v %s", plan, log.String())
	}
	if _, err := os.Stat(filepath.Join(opts.StagesDir(stagedCase), "ring")); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the stale stage was kept")
	}
	// -restage discards every bundle; -fresh keeps them.
	fresh := opts
	fresh.Fresh = true
	if plan, _ := planStage(stagedCase, fresh, resumption{}, &log); !plan.staged() {
		t.Fatal("-fresh discarded the stages")
	}
	restage := opts
	restage.Restage = true
	if plan, _ := planStage(stagedCase, restage, resumption{}, &log); plan.staged() || plan.key == "" {
		t.Fatalf("%+v", plan)
	}
	if _, err := os.Stat(opts.StagesDir(stagedCase)); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("-restage kept the bundles")
	}
	// The environment switches the cache off without touching the bundles.
	seedStage(t, opts, "shell", "", 3*time.Minute)
	t.Setenv(StagesEnv, "0")
	if plan, _ := planStage(stagedCase, opts, resumption{}, &log); plan.staged() || plan.key != "" || plan.off == "" {
		t.Fatalf("%+v", plan)
	}
}

func TestPlanStageDiscardsOnFingerprintMismatch(t *testing.T) {
	opts := ringRoot(t)
	seedStage(t, opts, "shell", "", 3*time.Minute)
	moved := stagedCase
	moved.Start = Save{Name: "other"}
	var log bytes.Buffer
	if plan, _ := planStage(moved, opts, resumption{}, &log); plan.staged() || !strings.Contains(log.String(), "the case's Start changed") {
		t.Fatalf("%+v %s", plan, log.String())
	}
	if _, err := os.Stat(filepath.Join(opts.StagesDir(stagedCase), "shell")); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the mismatched stage was kept")
	}
}

// Stage skips the blocks a hit covers, refuses undeclared and out-of-order
// names, and captures each later block into the stages ring.
func TestSessionStageSkipsHitAndCapturesMiss(t *testing.T) {
	opts := ringRoot(t)
	entry := seedStage(t, opts, "shell", "", 3*time.Minute)
	var saved []string
	stages := &na.CheckpointRing{
		Dir: opts.StagesDir(stagedCase), Case: stagedCase.Name, StageKey: "key",
		Config: &na.Config{Root: opts.Root, Headless: true, Configuration: opts.configDir(stagedCase)},
		Saver: func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error) {
			saved = append(saved, label)
			path := filepath.Join(opts.Root, "headless-profile", "Saves", name+".rws")
			if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
				return "", 0, nil, err
			}
			return path, 7000, nil, os.WriteFile(path, []byte(label), 0644)
		},
	}
	s := &session{c: stagedCase, Session: &na.Session{Game: &na.Game{}}, stagePlan: staging{key: "key", hit: 0, entry: entry}, stagesDir: opts.StagesDir(stagedCase), stages: stages, runStarted: time.Now()}
	ctx := context.Background()
	if err := s.Stage(ctx, "roof", func(context.Context) error { return nil }); err == nil || !strings.Contains(err.Error(), "not declared") {
		t.Fatal(err)
	}
	if err := s.Stage(ctx, "ring", func(context.Context) error { return nil }); err == nil || !strings.Contains(err.Error(), "out of order") {
		t.Fatal(err)
	}
	ran := false
	if err := s.Stage(ctx, "shell", func(context.Context) error { ran = true; return nil }); err != nil || ran {
		t.Fatal(err, ran)
	}
	if err := s.Stage(ctx, "ring", func(context.Context) error { ran = true; return nil }); err != nil || !ran {
		t.Fatal(err, ran)
	}
	if strings.Join(saved, ",") != "ring" {
		t.Fatal(saved)
	}
	captured, err := na.ReadCheckpoint(filepath.Join(opts.StagesDir(stagedCase), "ring"))
	if err != nil || captured.Stage != "ring" || captured.StageKey != "key" || captured.Tick != 7000 || captured.OffsetMs < 0 {
		t.Fatalf("%+v %v", captured, err)
	}
	if len(s.stageRows) != 2 || s.stageRows[0]["outcome"] != "hit" || s.stageRows[1]["outcome"] != "captured" || s.stageRows[1]["path"] != captured.Path {
		t.Fatalf("%v", s.stageRows)
	}
	// A block that fails leaves no bundle and reports so.
	s2 := &session{c: stagedCase, Session: &na.Session{Game: &na.Game{}}, stagePlan: staging{key: "key", hit: -1}, stagesDir: opts.StagesDir(stagedCase), stages: stages}
	if err := s2.Stage(ctx, "shell", func(context.Context) error { return errors.New("no site") }); err == nil || s2.stageRows[0]["outcome"] != "failed" {
		t.Fatal(err, s2.stageRows)
	}
	// With staging off the block runs and nothing is captured.
	s3 := &session{c: stagedCase, Session: &na.Session{Game: &na.Game{}}, stagePlan: staging{hit: -1}, stagesDir: opts.StagesDir(stagedCase)}
	saved = nil
	if err := s3.Stage(ctx, "shell", func(context.Context) error { return nil }); err != nil || s3.stageRows[0]["outcome"] != "uncached" || len(saved) != 0 {
		t.Fatal(err, s3.stageRows, saved)
	}
}

func TestValidateStages(t *testing.T) {
	run := func(context.Context, Session) error { return nil }
	for _, c := range []Case{
		{Name: "a/b", Start: Save{Name: "s"}, Run: run, Stages: []string{"x", "x"}},
		{Name: "a/b", Start: Save{Name: "s"}, Run: run, Stages: []string{""}},
		{Name: "a/b", Start: Save{Name: "s"}, Run: run, Stages: []string{"x/y"}},
		{Name: "a/b", Start: Owned{}, NoKeep: true, Run: run, Stages: []string{"x"}},
	} {
		if err := c.Validate(); err == nil {
			t.Errorf("%v validated", c.Stages)
		}
	}
	if err := (Case{Name: "a/b", Start: Save{Name: "s"}, Run: run, Stages: []string{"x", "y"}}).Validate(); err != nil {
		t.Error(err)
	}
}
