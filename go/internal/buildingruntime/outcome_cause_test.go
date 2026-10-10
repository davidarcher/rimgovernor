package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Every refusal and wait kind is a policy.Cause under its own string.
func TestEveryRefusalAndWaitKindIsACause(t *testing.T) {
	if len(refusalKinds)+len(waitKinds) != 23 {
		t.Fatalf("%d kinds, want 23", len(refusalKinds)+len(waitKinds))
	}
	for _, k := range append(append([]RefusalKind{}, refusalKinds...), waitKinds...) {
		if err := policy.Cause(k).Validate(); err != nil {
			t.Errorf("kind %q: %v", k, err)
		}
	}
}
