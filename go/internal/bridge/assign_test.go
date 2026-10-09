package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A bedroom swap carries the swap flag native evicts the owner on;
// a plain assignment leaves it unset.
func TestAssignCarriesSwapFlag(t *testing.T) {
	previous, err := domain.KnownPrevious("Bed1")
	if err != nil {
		t.Fatal(err)
	}
	plain, err := domain.NewAssign("Human1", "Bed2", previous)
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		assign domain.Assign
		swap   bool
	}{{plain, false}, {plain.AsSwap(), true}} {
		action, err := domain.NewAssignAction("a1", tc.assign)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		b := wire.GetAssign()
		if b.GetPawnId() != "Human1" || b.GetThingId() != "Bed2" || b.GetExpectedPrevious().GetEntityId() != "Bed1" || (b.Swap != nil) != tc.swap || b.GetSwap() != tc.swap {
			t.Fatalf("%v", wire)
		}
	}
}
