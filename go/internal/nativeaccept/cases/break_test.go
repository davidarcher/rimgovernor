package cases

import (
	"bytes"
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func TestCheckBreakValidates(t *testing.T) {
	staged := Case{Name: "a/b", Start: Save{Name: "seed"}, Stages: []string{"sited", "roofed"}}
	for name, tc := range map[string]struct {
		c    Case
		opts Options
		want string
	}{
		"none":              {ringCase, Options{}, ""},
		"stage declared":    {staged, Options{Break: na.Breakpoint{Stage: "roofed"}}, ""},
		"stage unknown":     {staged, Options{Break: na.Breakpoint{Stage: "walled"}}, "declares the stages [sited roofed]"},
		"stage undeclared":  {ringCase, Options{Break: na.Breakpoint{Stage: "roofed"}}, "declares no Stages"},
		"never checkpoints": {Case{Name: "a/b", Start: Save{Name: "seed"}, NoCheckpoint: true}, Options{Break: na.Breakpoint{Tick: 1}}, "never checkpoints"},
		"postmortem":        {ringCase, Options{Break: na.Breakpoint{Tick: 1}, PostmortemOnly: true}, "plain run"},
		"dev":               {ringCase, Options{Break: na.Breakpoint{Tick: 1}, Dev: true}, "plain run"},
	} {
		opts := tc.opts
		err := checkBreak(tc.c, &opts)
		switch {
		case tc.want == "" && err != nil:
			t.Errorf("%s: %v", name, err)
		case tc.want != "" && (err == nil || !strings.Contains(err.Error(), tc.want)):
			t.Errorf("%s: %v, want %q", name, err, tc.want)
		}
	}
	// The bundle is a ring entry, so a break turns the ring on.
	opts := Options{Break: na.Breakpoint{Minute: time.Minute}}
	if err := checkBreak(ringCase, &opts); err != nil || opts.CheckpointEvery != na.DefaultCheckpointEvery {
		t.Fatalf("%+v %v", opts, err)
	}
}

// A run cut at its breakpoint leaves the ring with the break bundle as its
// next entry after the timeline it resumed into, Break recording why; the
// next plan resumes from it and says so.
func TestBreakRingWritesNextAndResumes(t *testing.T) {
	opts := ringRoot(t)
	opts.Break = na.Breakpoint{Minute: 8 * time.Minute}
	previous := seedRing(t, opts, []string{"t+5m", "t+6m", "t+7m"}, "t+7m", 9000)
	resumed := resumption{entry: previous.Entries[2], previous: previous}
	ring := testRing(opts, 8*time.Minute, func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error) {
		if !force {
			return "", 0, nil, errors.New("a break bundle pauses the game itself")
		}
		path := filepath.Join(opts.Root, "headless-profile", "Saves", name+".rws")
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			return "", 0, nil, err
		}
		return path, 12000, nil, os.WriteFile(path, []byte(label), 0644)
	})
	// The bundle is stamped with this root's fingerprint, as a real ring's is.
	fp, err := fingerprint(ringCase, opts.configDir(ringCase))
	if err != nil {
		t.Fatal(err)
	}
	ring.Fingerprint = fp
	report := na.Report{}
	broke := &na.BreakError{Reason: "run phase reached t+8m"}
	if err := breakRing(context.Background(), ringCase, opts, ring, resumed, broke, report); err != nil {
		t.Fatal(err, report["checkpoint_errors"])
	}
	block, _ := report["break"].(map[string]any)
	if block["label"] != na.BreakCheckpoint || block["tick"] != uint64(12000) || block["spec"] != "minute=8" || block["reason"] != broke.Reason {
		t.Fatalf("break block %v", block)
	}
	rows, _ := report["checkpoints"].([]map[string]any)
	if len(rows) != 1 || rows[0]["label"] != na.BreakCheckpoint {
		t.Fatalf("checkpoints %v", rows)
	}
	next, err := na.ReadRing(ring.Dir)
	if err != nil || next == nil {
		t.Fatal(next, err)
	}
	var labels []string
	for _, e := range next.Entries {
		labels = append(labels, e.Label)
	}
	if strings.Join(labels, ",") != "t+5m,t+6m,t+7m,break" || next.Next != na.BreakCheckpoint || next.Failed != nil || next.ResumedFrom != "t+7m" {
		t.Fatalf("%+v", next)
	}
	if next.Break == nil || next.Break.Spec != "minute=8" || next.Break.Tick != 12000 || next.Break.Output != ring.Output {
		t.Fatalf("break record %+v", next.Break)
	}
	if !strings.Contains(next.Note, "paused at its breakpoint minute=8") || !strings.Contains(next.Note, "acceptance resume -root "+opts.Root) {
		t.Fatalf("note %q", next.Note)
	}
	var log bytes.Buffer
	plan, err := planResume(ringCase, opts, &log)
	if err != nil || !plan.resuming() || plan.entry.Label != na.BreakCheckpoint || plan.entry.Tick != 12000 {
		t.Fatalf("%+v %v\n%s", plan, err, log.String())
	}
	if !strings.Contains(log.String(), "resuming a/b from its breakpoint minute=8 (rev def456, tick 12000)") {
		t.Fatalf("log %q", log.String())
	}
	// The resumed run that then fails consumes the break bundle: the ring
	// it leaves holds the timeline and its own failed bundle only.
	failing := testRing(opts, 8*time.Minute, func(ctx context.Context, name, label string, force bool) (string, uint64, map[string]any, error) {
		path := filepath.Join(opts.Root, "headless-profile", "Saves", name+".rws")
		return path, 13000, nil, os.WriteFile(path, []byte(label), 0644)
	})
	report = na.Report{}
	closeRing(failing, plan, errors.New("stalled"), report, nil)
	after, err := na.ReadRing(ring.Dir)
	if err != nil || after == nil || after.Break != nil || after.Next != "t+7m" || after.Failed == nil {
		t.Fatalf("%+v %v", after, err)
	}
	if _, err := os.Stat(filepath.Join(ring.Dir, na.BreakCheckpoint)); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the break bundle outlived the run that resumed from it")
	}
}

// -postmortem-only over a paused run reads the break bundle by default.
func TestPostmortemBundleFallsBackToBreak(t *testing.T) {
	opts := ringRoot(t)
	ring := seedRing(t, opts, []string{"t+7m", na.BreakCheckpoint}, na.BreakCheckpoint, 0)
	ring.Break = &na.BreakRecord{Spec: "minute=8"}
	if err := ring.Write(opts.RingDir(ringCase)); err != nil {
		t.Fatal(err)
	}
	entry, _, err := postmortemBundle(ringCase, opts)
	if err != nil || entry.Path != filepath.Join(opts.RingDir(ringCase), na.BreakCheckpoint) {
		t.Fatalf("%+v %v", entry, err)
	}
}
