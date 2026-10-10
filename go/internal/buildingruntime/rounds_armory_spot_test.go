package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// MaintainEquipment stands the spot in the planned workshop, and deconstructs
// it once a weapon bench stands beside it.
func TestCraftingSpotSelection(t *testing.T) {
	t.Parallel()
	r := &RoundsBuildingPlanner{concern: policy.MaintainEquipment, definition: craftingSpotDefinition, craftingSpot: true}
	f := observation.ColonyProjection{Facts: policy.RoundsFacts{Colonists: domain.Known(int64(3))}}
	if n, id, reason := r.selection(f); n != 1 || id != "crafting-spot" || !reason.IsZero() {
		t.Fatal(n, id, reason)
	}
	spot, err := domain.NewBuilding(policy.CraftingSpotDefinition, domain.Cell{X: 4, Z: 5}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	f.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "spot", Building: spot, Cells: []domain.Cell{{X: 4, Z: 5}}}}})
	if _, retire := standingCraftingSpot(f); retire {
		t.Fatal("the spot was retired with no weapon bench standing")
	}
	f.Facts.CraftingSpotRetireOwed = domain.Known(true)
	if got, retire := standingCraftingSpot(f); !retire || got.ID != "spot" {
		t.Fatal("the spot beside a weapon bench was not retired", got, retire)
	}
	if n, id, reason := r.selection(f); n != 1 || id != "crafting-spot-retire" || !reason.IsZero() {
		t.Fatal(n, id, reason)
	}
}

// The crafting spot is the stand-in planner, not the production-bench ladder.
func TestCraftingSpotIsNotTheWorkshopLadder(t *testing.T) {
	t.Parallel()
	if (&RoundsBuildingPlanner{concern: policy.MaintainEquipment, craftingSpot: true}).workshopLadder() {
		t.Fatal("the spot planner walked the ladder")
	}
	if !(&RoundsBuildingPlanner{concern: policy.MaintainEquipment}).workshopLadder() {
		t.Fatal("the equipment ladder was lost")
	}
}
