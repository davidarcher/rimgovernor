package cases

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/postmortem"
)

func noop(context.Context, Session) error { return nil }

func TestRegisterRejectsDuplicateNames(t *testing.T) {
	reset()
	defer reset()
	Register(Case{Name: "a/one", Start: DebugStart{}, Run: noop})
	defer func() {
		if recover() == nil {
			t.Fatalf("second Register of a/one did not panic")
		}
	}()
	Register(Case{Name: "a/one", Start: Save{Name: "x"}, Run: noop})
}

func TestRegisterRejectsIncompleteCases(t *testing.T) {
	reset()
	defer reset()
	for _, c := range []Case{
		{Start: DebugStart{}, Run: noop},
		{Name: "a/nostart", Run: noop},
		{Name: "a/norun", Start: DebugStart{}},
	} {
		if err := c.Validate(); err == nil {
			t.Errorf("Validate(%+v) = nil", c)
		}
	}
}

func TestAllSortsAndLookupFinds(t *testing.T) {
	reset()
	defer reset()
	Register(Case{Name: "b/two", Start: DebugStart{}, Run: noop})
	Register(Case{Name: "a/one", Start: Fixture{Op: "test/x"}, Run: noop})
	all := All()
	if len(all) != 2 || all[0].Name != "a/one" || all[1].Name != "b/two" {
		t.Fatalf("All() = %v", all)
	}
	if c, ok := Lookup("b/two"); !ok || c.Name != "b/two" {
		t.Fatalf("Lookup(b/two) = %+v, %v", c, ok)
	}
	if _, ok := Lookup("c/none"); ok {
		t.Fatalf("Lookup(c/none) found a case")
	}
}

func TestExecuteRefusesLintFailureBeforeOpening(t *testing.T) {
	output := t.TempDir()
	c := Case{Name: "lint/nobudget", Scope: "lint", Start: DebugStart{}, Run: noop}
	report, code := Execute(context.Background(), c, Options{Root: output, Output: output, Timeout: time.Second})
	if code == 0 {
		t.Fatalf("Execute passed a case without a budget")
	}
	if err, _ := report["error"].(string); !strings.Contains(err, "Budget is missing") {
		t.Fatalf("error = %q", err)
	}
	if _, has := report["boot_ms"]; has {
		t.Fatalf("the game was opened for a case that fails lint: %v", report)
	}
	if _, err := os.Stat(filepath.Join(output, "lint", "nobudget", "result.json")); err != nil {
		t.Fatalf("no result.json: %v", err)
	}
	if stats, ok := report["wait_stats"].(map[string]any); !ok || stats["stalled"] != 0 {
		t.Fatalf("wait_stats = %#v", report["wait_stats"])
	}
}

func TestExecuteFailureWritesDiagnosis(t *testing.T) {
	output := t.TempDir()
	// A serve case without a binary fails inside execute, before any game
	// opens, which is enough for the failure path to collect its digest.
	c := Case{Name: "digest/noserve", Scope: "digest", Start: Save{Name: "missing"}, Budget: time.Minute, Serve: &ServeSpec{}, Run: noop}
	report, code := Execute(context.Background(), c, Options{Root: output, Output: output, Timeout: time.Second})
	if code == 0 {
		t.Fatalf("Execute passed a serve case without a binary")
	}
	digest, ok := report["diagnosis"].(postmortem.Digest)
	if !ok || digest.Error == "" || len(digest.Sections) == 0 {
		t.Fatalf("diagnosis = %#v", report["diagnosis"])
	}
	caseDir := filepath.Join(output, "digest", "noserve")
	text, err := os.ReadFile(filepath.Join(caseDir, "diagnosis.txt"))
	if err != nil || !strings.HasPrefix(string(text), "case: digest/noserve\nerror: ") {
		t.Fatalf("diagnosis.txt: %v\n%s", err, text)
	}
	result, err := os.ReadFile(filepath.Join(caseDir, "result.json"))
	if err != nil || !strings.HasPrefix(string(result), "{\n  \"diagnosis\": {") {
		t.Fatalf("result.json: %v\n%.80s", err, result)
	}
}
