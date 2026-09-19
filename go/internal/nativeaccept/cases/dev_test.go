package cases

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// planDev picks the ring's next entry, then its failed bundle, then what
// -from names, refusing a bundle another native package captured; the
// ring is never written.
func TestPlanDevResolvesBundle(t *testing.T) {
	opts := ringRoot(t)
	opts.Dev = true
	before := seedFailedRing(t, opts, "")
	var log bytes.Buffer

	resumed, err := planDev(ringCase, opts, &log)
	if err != nil || !resumed.resuming() || resumed.entry.Label != "t+1m" || resumed.previous != nil {
		t.Fatalf("default: %+v %v", resumed, err)
	}
	if !strings.Contains(log.String(), "dev: a/b from ") || !strings.Contains(log.String(), "the ring is untouched") {
		t.Errorf("log: %q", log.String())
	}

	opts.From = na.FailedCheckpoint
	if resumed, err := planDev(ringCase, opts, &log); err != nil || resumed.entry.Tick != 2000 {
		t.Fatalf("-from failed: %+v %v", resumed, err)
	}

	// With no next entry the failed bundle is the default.
	noNext := *before
	noNext.Next = ""
	if err := noNext.Write(opts.RingDir(ringCase)); err != nil {
		t.Fatal(err)
	}
	opts.From = ""
	if resumed, err := planDev(ringCase, opts, &log); err != nil || resumed.entry.Label != na.FailedCheckpoint {
		t.Fatalf("failed fallback: %+v %v", resumed, err)
	}
	if err := before.Write(opts.RingDir(ringCase)); err != nil {
		t.Fatal(err)
	}

	other := Case{Name: "c/d", Start: Save{Name: "seed"}}
	if _, err := planDev(other, opts, &log); err == nil || !strings.Contains(err.Error(), "no bundle of c/d") {
		t.Errorf("no ring: %v", err)
	}
	owned := Case{Name: "c/d", Start: Owned{}, NoKeep: true}
	if _, err := planDev(owned, opts, &log); err == nil || !strings.Contains(err.Error(), "never checkpoints") {
		t.Errorf("owned: %v", err)
	}

	stale := ringRoot(t)
	stale.Dev = true
	seedFailedRing(t, stale, "other-package")
	if _, err := planDev(ringCase, stale, &log); err == nil || !strings.Contains(err.Error(), "the installed native package changed") {
		t.Errorf("stale package: %v", err)
	}

	after, err := na.ReadRing(opts.RingDir(ringCase))
	if err != nil || after == nil || after.Next != before.Next || after.Failed == nil || len(after.Entries) != len(before.Entries) {
		t.Fatalf("ring changed: %+v %v", after, err)
	}
}

// A dev iteration's output is numbered under <output>/dev, and Execute
// refuses a directory an earlier iteration filled.
func TestDevCaseOutput(t *testing.T) {
	opts := Options{Output: filepath.Join("out"), Dev: true}
	if got := opts.CaseOutput(ringCase); got != filepath.Join("out", "dev", "1", "a", "b") {
		t.Errorf("attempt 0: %s", got)
	}
	opts.Attempt = 3
	if got := opts.CaseOutput(ringCase); got != filepath.Join("out", "dev", "3", "a", "b") {
		t.Errorf("attempt 3: %s", got)
	}

	root := ringRoot(t)
	root.Dev, root.Attempt = true, 2
	root.Output = filepath.Join(root.Root, "acceptance")
	seedFailedRing(t, root, "")
	c := ringCase
	c.Budget = MaxBudget
	if err := os.MkdirAll(filepath.Join(root.CaseOutput(c), "old"), 0755); err != nil {
		t.Fatal(err)
	}
	report, code := Execute(t.Context(), c, root)
	if code == 0 || !strings.Contains(na.AsString(report["error"]), "is not empty") {
		t.Fatalf("report %v code %d", report["error"], code)
	}
}
