package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The butcher spot is its own goal: owed whenever no butcher bench stands
// apart from the kitchen, whatever the food runway.
func TestButcherSpotGoalIsOwedUntilABenchStandsApart(t *testing.T) {
	f := stableRoutine()
	f.ButcherBenches = domain.Known([]ButcherBench{})
	r := needs(t, f, RoutineLatches{})
	if !hasNeed(r, MaintainButcherSpot) {
		t.Fatal("no butcher bench, goal not raised", r.Goals)
	}
	f.ButcherBenches = domain.Known([]ButcherBench{{ID: "spot"}})
	if r = needs(t, f, RoutineLatches{}); hasNeed(r, MaintainButcherSpot) {
		t.Fatal("a standing spot left the goal open")
	}
	f.ButcherBenches = domain.Unknown[[]ButcherBench]()
	if got := butcherSpotMet(f); got != domain.Unknown[bool]() {
		t.Fatal("an unread census became evidence", got)
	}
}

// A stand-in spot left standing apart keeps the goal open for the table once
// the wood is in stock, and the table closes it.
func TestButcherSpotGoalStaysOpenForTheTable(t *testing.T) {
	f := stableRoutine()
	f.Upkeep.Rooms = domain.Known(RoomObservation{})
	f.ButcherBenches = domain.Known([]ButcherBench{{ID: "spot", Definition: "ButcherSpot"}})
	f.Wood = domain.Known(ButcherTableWood)
	if got := butcherSpotMet(f); got != domain.Known(false) {
		t.Fatal("a spot with the wood for a table closed the goal", got)
	}
	f.Wood = domain.Known(ButcherTableWood - 1)
	if got := butcherSpotMet(f); got != domain.Known(true) {
		t.Fatal("a spot without the wood held the goal open", got)
	}
	f.Wood = domain.Known(ButcherTableWood)
	f.ButcherBenches = domain.Known([]ButcherBench{{ID: "table", Definition: "TableButcher"}})
	if got := butcherSpotMet(f); got != domain.Known(true) {
		t.Fatal("a standing table left the goal open", got)
	}
}

// A table standing beside its stand-in spot leaves the goal open for the
// spot's removal.
func TestButcherSpotGoalOwesTheSpotsRemovalBesideATable(t *testing.T) {
	f := stableRoutine()
	f.Upkeep.Rooms = domain.Known(RoomObservation{})
	f.Wood = domain.Known(int64(0))
	f.ButcherBenches = domain.Known([]ButcherBench{{ID: "spot", Definition: "ButcherSpot"}, {ID: "table", Definition: "TableButcher"}})
	if got := butcherSpotMet(f); got != domain.Known(false) {
		t.Fatal("a spot beside a table left the goal closed", got)
	}
}
