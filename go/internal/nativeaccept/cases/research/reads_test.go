package research

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

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
