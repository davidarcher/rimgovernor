package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A bedroom swap carries the swap flag native evicts the owner on (#1243);
// a plain assignment leaves it unset.
func TestBedAssignCarriesSwapFlag(t *testing.T) {
	previous, err := domain.KnownPreviousBed("Bed1")
	if err != nil {
		t.Fatal(err)
	}
	plain, err := domain.NewBedAssign("Human1", "Bed2", previous)
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		assign domain.BedAssign
		swap   bool
	}{{plain, false}, {plain.AsSwap(), true}} {
		action, err := domain.NewBedAssignAction("a1", tc.assign)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		b := wire.GetBedAssign()
		if b.GetPawnId() != "Human1" || b.GetBedId() != "Bed2" || b.GetExpectedPreviousBed().GetEntityId() != "Bed1" || (b.Swap != nil) != tc.swap || b.GetSwap() != tc.swap {
			t.Fatalf("%v", wire)
		}
	}
}
