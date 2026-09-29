package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The bedroom swap flag (#1243) survives a plan round trip.
func TestBedAssignSwapRoundTrips(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	for i, swap := range []bool{false, true} {
		assign, err := domain.NewBedAssign("Human1", "Bed2", domain.ClearPreviousBed())
		if err != nil {
			t.Fatal(err)
		}
		if swap {
			assign = assign.AsSwap()
		}
		id := domain.PlanID([]string{"bed-plain", "bed-swap"}[i])
		action, err := domain.NewBedAssignAction(domain.ActionID(string(id)+"-0"), assign)
		if err != nil {
			t.Fatal(err)
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CreatePlan(ctx, plan); err != nil {
			t.Fatal(err)
		}
		loaded, err := s.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(err)
		}
		got, ok := loaded.Progress[0].Action().BedAssign()
		if !ok || got.Swap() != swap || got.Bed() != "Bed2" || !got.PreviousBed().Clear() {
			t.Fatal(swap, got)
		}
	}
}
