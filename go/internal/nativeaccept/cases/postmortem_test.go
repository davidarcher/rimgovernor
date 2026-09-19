package cases

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// seedFailedRing writes a ring of ringCase with a periodic entry and a
// failed bundle, each sidecar stamped with the current fingerprint (or
// pkg when set), and a failed run's result.json the index points at.
func seedFailedRing(t *testing.T, opts Options, pkg string) *na.Ring {
	t.Helper()
	fp, err := fingerprint(ringCase, opts.configDir(ringCase))
	if err != nil {
		t.Fatal(err)
	}
	if pkg != "" {
		fp.Package = pkg
	}
	dir := opts.RingDir(ringCase)
	output := filepath.Join(opts.Root, "acceptance-1", "a", "b")
	if err := os.MkdirAll(output, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(output, "result.json"), []byte(`{"timeline": [{"plans": []}], "error": "stalled"}`), 0644); err != nil {
		t.Fatal(err)
	}
	ring := &na.Ring{Case: ringCase.Name, Next: "t+1m", FailedTick: 2000, FailedOffsetMs: 90000, SourceRevision: "abc123", FailedOutput: output}
	write := func(label string, tick uint64) na.Checkpoint {
		entry := na.Checkpoint{Case: ringCase.Name, Label: label, OffsetMs: 60000, Tick: tick, Save: na.CheckpointSaveName(ringCase.Name), Store: true,
			State: map[string]any{"baseline": 3.0}, Package: fp.Package, Start: fp.Start, Expansions: fp.Expansions, Path: filepath.Join(dir, label)}
		if err := os.MkdirAll(entry.Path, 0755); err != nil {
			t.Fatal(err)
		}
		data, _ := json.Marshal(entry)
		for name, body := range map[string][]byte{na.CheckpointSidecar: data, entry.Save + ".rws": []byte(label), na.CheckpointStoreFile: []byte("db")} {
			if err := os.WriteFile(filepath.Join(entry.Path, name), body, 0644); err != nil {
				t.Fatal(err)
			}
		}
		return entry
	}
	ring.Entries = []na.Checkpoint{write("t+1m", 1000)}
	failed := write(na.FailedCheckpoint, 2000)
	ring.Failed = &failed
	if err := ring.Write(dir); err != nil {
		t.Fatal(err)
	}
	return ring
}

func TestPostmortemBundleResolvesFromFlag(t *testing.T) {
	opts := ringRoot(t)
	ring := seedFailedRing(t, opts, "")

	entry, index, err := postmortemBundle(ringCase, opts)
	if err != nil || entry.Label != na.FailedCheckpoint || entry.Tick != 2000 || index == nil || index.FailedOutput != ring.FailedOutput {
		t.Fatalf("default: %+v %+v %v", entry, index, err)
	}

	opts.From = "t+1m"
	if entry, index, err := postmortemBundle(ringCase, opts); err != nil || entry.Tick != 1000 || index == nil {
		t.Fatalf("label: %+v %v", entry, err)
	}

	opts.From = filepath.Join(opts.RingDir(ringCase), "t+1m")
	if entry, index, err := postmortemBundle(ringCase, opts); err != nil || entry.Tick != 1000 || index == nil {
		t.Fatalf("path in the ring: %+v %v", entry, err)
	}

	// A bundle outside the ring loads without the ring's index.
	elsewhere := filepath.Join(t.TempDir(), "kept")
	if err := os.Rename(filepath.Join(opts.RingDir(ringCase), "t+1m"), elsewhere); err != nil {
		t.Fatal(err)
	}
	opts.From = elsewhere
	if entry, index, err := postmortemBundle(ringCase, opts); err != nil || entry.Tick != 1000 || index != nil {
		t.Fatalf("path outside the ring: %+v %+v %v", entry, index, err)
	}

	opts.From = "t+9m"
	if _, _, err := postmortemBundle(ringCase, opts); err == nil || !strings.Contains(err.Error(), "no bundle there") {
		t.Errorf("missing label: %v", err)
	}

	opts.From = ""
	other := Case{Name: "c/d", Start: Save{Name: "seed"}}
	if _, _, err := postmortemBundle(other, opts); err == nil || !strings.Contains(err.Error(), "no failed bundle of c/d") {
		t.Errorf("no ring: %v", err)
	}
	opts.From = elsewhere
	if _, _, err := postmortemBundle(other, opts); err == nil || !strings.Contains(err.Error(), "is a bundle of a/b, not c/d") {
		t.Errorf("wrong case: %v", err)
	}
}

// executePostmortem refuses before touching the game: a case without the
// phase, a missing bundle, a bundle taken under another native package.
// The ring is left as it was on every path.
func TestExecutePostmortemRefusesWithoutTouchingTheRing(t *testing.T) {
	opts := ringRoot(t)
	opts.PostmortemOnly = true
	before := seedFailedRing(t, opts, "")
	report := na.Report{}
	output := filepath.Join(opts.Root, "out")

	err := executePostmortem(context.Background(), ringCase, opts, output, report)
	if err == nil || !strings.Contains(err.Error(), "declares no Postmortem phase") {
		t.Errorf("no phase: %v", err)
	}

	split := ringCase
	split.Postmortem = func(context.Context, Session) error { return nil }
	opts.From = "t+7m"
	if err := executePostmortem(context.Background(), split, opts, output, report); err == nil || !strings.Contains(err.Error(), "no bundle there") {
		t.Errorf("missing bundle: %v", err)
	}

	stale := ringRoot(t)
	stale.PostmortemOnly = true
	seedFailedRing(t, stale, "other-package")
	if err := executePostmortem(context.Background(), split, stale, filepath.Join(stale.Root, "out"), report); err == nil || !strings.Contains(err.Error(), "the installed native package changed") {
		t.Errorf("stale package: %v", err)
	}

	after, err := na.ReadRing(opts.RingDir(ringCase))
	if err != nil || after == nil || after.Next != before.Next || after.Failed == nil || len(after.Entries) != len(before.Entries) {
		t.Fatalf("ring changed: %+v %v", after, err)
	}
	if _, ok := report["postmortem_only"]; ok {
		t.Error("a refused run must not be marked postmortem_only")
	}
}

func TestExecuteRefusesPostmortemOnlyWithoutOutput(t *testing.T) {
	// Execute's fresh-output check runs before the mode is consulted; the
	// staged store lands under the output, so an empty one is required.
	opts := ringRoot(t)
	opts.PostmortemOnly = true
	opts.Output = filepath.Join(opts.Root, "acceptance")
	seedFailedRing(t, opts, "")
	split := ringCase
	split.Budget = MaxBudget
	split.Postmortem = func(context.Context, Session) error { return nil }
	if err := os.MkdirAll(filepath.Join(opts.CaseOutput(split), "old"), 0755); err != nil {
		t.Fatal(err)
	}
	report, code := Execute(context.Background(), split, opts)
	if code == 0 || !strings.Contains(na.AsString(report["error"]), "is not empty") {
		t.Fatalf("report %v code %d", report["error"], code)
	}
}
