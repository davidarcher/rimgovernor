package main

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// capabilityFixture mirrors test_native_research_evidence.py's capability(): one
// researcher present with a known-zero priority and known-false active/disabled, to
// prove those legitimate falsy facts are not confused with a missing field.
func capabilityFixture() (map[string]any, map[string]any) {
	row := map[string]any{"intellectual": 5.0, "priority": 0.0, "disabled": false, "everWork": true, "active": false}
	observedRow := copyResearchAny(row)
	observedRow["pawn"] = map[string]any{"id": "Thing_Pawn1"}
	nativeRow := copyResearchAny(row)
	nativeRow["thingId"] = "Pawn1"
	return map[string]any{"researchers": []any{observedRow}},
		map[string]any{"researchBenches": map[string]any{"readable": true, "count": 0.0, "benches": []any{}, "researchers": []any{nativeRow}}}
}

func copyResearchAny(m map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range m {
		out[k] = v
	}
	return out
}

func TestFingerprintStateComparesDeclaredFieldsNotOperationMetadata(t *testing.T) {
	before := map[string]any{"success": true, "current": nil, "progress": []any{}, "knowledge": []any{}, "slots": nil, "techprints": []any{}, "tick": 1.0, "paused": true, "operation": map[string]any{"OperationId": "first"}}
	after := copyResearchAny(before)
	after["operation"] = map[string]any{"OperationId": "second"}
	fpBefore, err := fingerprintState(before)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	fpAfter, err := fingerprintState(after)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !na.DeepEqual(fpBefore, fpAfter) {
		t.Fatal("expected fingerprints to match despite differing operation metadata")
	}
	after["slots"] = []any{}
	fpAfter, err = fingerprintState(after)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if na.DeepEqual(fpBefore, fpAfter) {
		t.Fatal("expected fingerprints to differ once slots actually changed")
	}
	delete(after, "progress")
	if _, err := fingerprintState(after); err == nil {
		t.Fatal("expected an error for a fixture missing a declared field")
	}
}

func TestCompareCapabilityKnownZeroResearchPriorityIsValid(t *testing.T) {
	observed, legacy := capabilityFixture()
	if err := compareCapability(observed, legacy); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestCompareCapabilityMissingResearcherFactsCannotImplyZeroOrFalse(t *testing.T) {
	for _, field := range []string{"intellectual", "priority", "disabled", "everWork", "active"} {
		t.Run(field, func(t *testing.T) {
			observed, legacy := capabilityFixture()
			researchers := observed["researchers"].([]any)
			row := researchers[0].(map[string]any)
			delete(row, field)
			if err := compareCapability(observed, legacy); err == nil {
				t.Fatalf("expected an error for missing field %q", field)
			}
		})
	}
}

func finishedProjectsFixture() (map[string]any, map[string]any) {
	observed := map[string]any{
		"projects": []any{map[string]any{
			"project": map[string]any{"defName": "Finished"}, "finished": true, "canStart": false, "available": false,
		}},
		"completeness": map[string]any{"page": map[string]any{"complete": true}, "matched": "1", "returned": "1", "unreadable": "0"},
		"slots":        []any{map[string]any{}}, "anomalyActive": false,
	}
	legacy := map[string]any{"available": []any{}, "locked": []any{}, "finished": []any{"Finished"}, "current": nil, "currentByCategory": map[string]any{}, "anomalyActive": false}
	return observed, legacy
}

func TestCompareProjectsFinishedCensusRequiresCompleteNativeSetAndClosedGate(t *testing.T) {
	observed, legacy := finishedProjectsFixture()
	if err := compareProjects(observed, legacy); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, field := range []string{"canStart", "available"} {
		t.Run(field, func(t *testing.T) {
			bad, legacy := finishedProjectsFixture()
			rows := bad["projects"].([]any)
			row := rows[0].(map[string]any)
			row[field] = true
			if err := compareProjects(bad, legacy); err == nil {
				t.Fatalf("expected an error for a finished project reporting %s=true", field)
			}
		})
	}
	observed, legacy = finishedProjectsFixture()
	finished := legacy["finished"].([]any)
	legacy["finished"] = append(finished, "Missing")
	if err := compareProjects(observed, legacy); err == nil {
		t.Fatal("expected an error for a legacy finished project missing from the typed read")
	}
}
