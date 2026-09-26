package cases

import (
	"strings"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func provenanceCase() Case {
	return Case{
		Name:   "fixture/provenance",
		Scope:  "a claim",
		Start:  Fixture{Op: "test/shortage_prepare", On: Fixture{Op: "test/colony_prepare"}},
		Stages: []string{"shortage", "rebuilt"},
	}
}

func stamp(t *testing.T, c Case, report na.Report) map[string]any {
	t.Helper()
	block := Provenance(c, report)
	if same, _ := report[ProvenanceKey].(map[string]any); len(same) != len(block) {
		t.Fatalf("Provenance did not stamp the report: %v", report[ProvenanceKey])
	}
	return block
}

// A run that drove the case itself proves the whole case, names the ops the
// installed package had to register, and says it shared the kept process.
func TestProvenanceFullRun(t *testing.T) {
	block := stamp(t, provenanceCase(), na.Report{"keep": true})
	if block["execution"] != string(ExecutionFull) {
		t.Fatalf("execution: %v", block["execution"])
	}
	if block["world"] != "fresh" || block["process"] != "reused-after-run" {
		t.Fatalf("world/process: %v %v", block["world"], block["process"])
	}
	if block["isolation"] != string(IsolationShared) {
		t.Fatalf("isolation: %v", block["isolation"])
	}
	if _, has := block["isolation_reason"]; has {
		t.Fatalf("a shared-process case needs no isolation reason: %v", block["isolation_reason"])
	}
	ops, _ := block["native_ops"].([]string)
	if len(ops) != 2 || ops[0] != "test/colony_prepare" || ops[1] != "test/shortage_prepare" {
		t.Fatalf("native_ops: %v", ops)
	}
}

// A resumed run says the suffix it covers and where it picked up, so a
// changed early behaviour is visibly not proved. The row is still recorded,
// not rejected: the landing lane decides policy, this block only states
// what the result covers.
func TestProvenanceResumedSuffixNamesThePickupPoint(t *testing.T) {
	report := na.Report{"keep": true, "resumed_from": map[string]any{"label": "t+7m", "tick": float64(184000), "offset_ms": float64(420000)}}
	block := stamp(t, provenanceCase(), report)
	if block["execution"] != string(ExecutionResumedSuffix) {
		t.Fatalf("execution: %v", block["execution"])
	}
	proves, _ := block["proves"].(string)
	if !strings.Contains(proves, "tick 184000") || !strings.Contains(proves, "past") {
		t.Fatalf("proves: %q", proves)
	}
	if block["world"] != "restored-save" {
		t.Fatalf("world: %v", block["world"])
	}
	// Without a tick the label stands in; an unlabelled entry falls back to
	// the run offset.
	block = stamp(t, provenanceCase(), na.Report{"resumed_from": map[string]any{"offset_ms": float64(90000)}})
	if proves, _ = block["proves"].(string); !strings.Contains(proves, "t+90s") {
		t.Fatalf("offset fallback: %q", proves)
	}
}

// A cached precondition proves nothing about the staging blocks that
// produced it: the block names them, which is what tells a reviewer that a
// changed planner cannot be validated by reusing its own shell.
func TestProvenanceCachedPreconditionNamesTheSkippedStages(t *testing.T) {
	report := na.Report{"keep": true, "staged_from": map[string]any{"stage": "shortage", "tick": float64(1000)}}
	block := stamp(t, provenanceCase(), report)
	if block["execution"] != string(ExecutionCachedPrecondition) {
		t.Fatalf("execution: %v", block["execution"])
	}
	proves, _ := block["proves"].(string)
	if !strings.Contains(proves, `"shortage"`) || !strings.Contains(proves, "not rerun") {
		t.Fatalf("proves: %q", proves)
	}
	if strings.Contains(proves, "rebuilt") {
		t.Fatalf("a bundle of the first stage does not stand in for a later one: %q", proves)
	}
	// A bundle of the last stage stands in for every stage before it too.
	block = stamp(t, provenanceCase(), na.Report{"staged_from": map[string]any{"stage": "rebuilt"}})
	if proves, _ = block["proves"].(string); !strings.Contains(proves, "shortage -> rebuilt") {
		t.Fatalf("proves: %q", proves)
	}
}

// An iteration loop and a reads-only rerun are never evidence for the
// scenario, and postmortem-only wins over the bundle it reloaded.
func TestProvenanceIterationsAreNotEvidence(t *testing.T) {
	block := stamp(t, provenanceCase(), na.Report{"dev": true, "staged_from": map[string]any{"stage": "shortage"}})
	if block["execution"] != string(ExecutionDev) {
		t.Fatalf("dev: %v", block["execution"])
	}
	block = stamp(t, provenanceCase(), na.Report{"postmortem_only": true, "resumed_from": map[string]any{"label": "failed"}})
	if block["execution"] != string(ExecutionPostmortemOnly) {
		t.Fatalf("postmortem-only: %v", block["execution"])
	}
	if proves, _ := block["proves"].(string); !strings.Contains(proves, "not rerun") {
		t.Fatalf("proves: %q", proves)
	}
}

// Isolation is reported with the reason, because a map reload is not a
// process reset: a case asserting on initialization must be visibly on its
// own process.
func TestProvenanceIsolationReportsWhyTheProcessIsOwned(t *testing.T) {
	for _, tc := range []struct {
		name   string
		c      Case
		reason string
	}{
		{"nokeep", Case{Name: "a/b", Start: DebugStart{}, NoKeep: true}, "NoKeep"},
		{"dlc", Case{Name: "a/b", Start: DebugStart{}, NoKeep: true, Expansions: []string{"royalty"}}, "DLC profile"},
		{"owned", Case{Name: "a/b", Start: Owned{}, NoKeep: true}, "lifecycle itself"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			block := stamp(t, tc.c, na.Report{})
			if block["isolation"] != string(IsolationFresh) {
				t.Fatalf("isolation: %v", block["isolation"])
			}
			reason, _ := block["isolation_reason"].(string)
			if !strings.Contains(reason, tc.reason) {
				t.Fatalf("isolation_reason %q lacks %q", reason, tc.reason)
			}
			if block["process"] != "not-kept-after-run" {
				t.Fatalf("process: %v", block["process"])
			}
		})
	}
}

// A serve-driven case restricted to some routine families says so: the
// result is silent about every family that was disabled.
func TestProvenanceRecordsRestrictedRoutineFamilies(t *testing.T) {
	c := provenanceCase()
	c.Serve = &ServeSpec{Families: []string{"supply", "safety"}}
	block := stamp(t, c, na.Report{})
	families, _ := block["routine_families"].([]string)
	if len(families) != 2 || families[0] != "supply" {
		t.Fatalf("routine_families: %v", families)
	}
	c.Serve = &ServeSpec{}
	block = stamp(t, c, na.Report{})
	if _, has := block["routine_families"]; has {
		t.Fatalf("a spec that composes every family declares no restriction: %v", block["routine_families"])
	}
}

// Every registered case can be described, and the description of a full run
// never claims more than the whole case.
func TestProvenanceCoversEveryRegisteredCase(t *testing.T) {
	for _, c := range All() {
		block := Provenance(c, na.Report{"keep": !c.NoKeep})
		if block["execution"] != string(ExecutionFull) {
			t.Fatalf("%s: %v", c.Name, block["execution"])
		}
		if block["isolation"] == "" || block["proves"] == "" {
			t.Fatalf("%s: incomplete provenance %v", c.Name, block)
		}
	}
}
