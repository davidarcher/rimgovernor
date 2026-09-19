package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestBasicComfortFurnishesAnyRoomAndSitesRecreationAnywhere(t *testing.T) {
	t.Parallel()
	planner := &RoutineBuildingPlanner{goal: policy.EnsureBasicComfort}
	people := []policy.PawnID{"pawn"}
	census := policy.ComfortObservation{People: people}
	// The hosted census is empty (the hut is a barracks); the foothold goal
	// reads the unfiltered one.
	facts := observation.ColonyProjection{Facts: policy.RoutineFacts{Comfort: domain.Known(policy.ComfortObservation{People: people}), BasicComfort: domain.Known(census)}, Definitions: []observation.PlanningDefinition{{Name: "Table1x2c", Stuff: domain.Known("WoodLog")}, {Name: "DiningChair", Stuff: domain.Known("WoodLog")}}}
	selected, reason, err := planner.selectBasicComfort(facts)
	if err != nil || reason != "" || selected.definition != "Table1x2c" || selected.stuff != "WoodLog" || selected.environment != policy.PlacementIndoors || selected.facility != nil {
		t.Fatal(selected, reason, err)
	}
	census.Surfaces = []policy.DiningSurface{{ID: "table", Adjacent: []domain.Cell{{X: 2, Z: 3}}}}
	facts.Facts.BasicComfort = domain.Known(census)
	selected, reason, err = planner.selectBasicComfort(facts)
	if err != nil || reason != "" || selected.definition != "DiningChair" || len(selected.adjacent) != 1 || selected.adjacent[0] != (domain.Cell{X: 2, Z: 3}) {
		t.Fatal(selected, reason, err)
	}
	census.Dining = []policy.ComfortFacility{{ID: "chair", AccessibleTo: people}}
	facts.Facts.BasicComfort = domain.Known(census)
	selected, reason, err = planner.selectBasicComfort(facts)
	if err != nil || reason != "" || selected.definition != "HorseshoesPin" || selected.environment != policy.PlacementAnywhere || selected.facility != nil {
		t.Fatal(selected, reason, err)
	}
	// A reachable, unused pin is provided: no wait for observed use.
	census.Recreation = []policy.ComfortFacility{{ID: "pin", AccessibleTo: people}}
	facts.Facts.BasicComfort = domain.Known(census)
	selected, reason, err = planner.selectBasicComfort(facts)
	if err != nil || reason != BuildingMethodNoDeficit || selected != nil {
		t.Fatal(selected, reason, err)
	}
	census.Recreation[0].AccessibleTo = nil
	facts.Facts.BasicComfort = domain.Known(census)
	selected, reason, err = planner.selectBasicComfort(facts)
	if err != nil || reason != BuildingExistingFacility || selected != nil {
		t.Fatal(selected, reason, err)
	}
	facts.Facts.BasicComfort = domain.Unknown[policy.ComfortObservation]()
	if _, reason, err = planner.selectBasicComfort(facts); err != nil || reason != BuildingMethodUnknown {
		t.Fatal(reason, err)
	}
	if planner.definition != "" || planner.stuff != "" || len(planner.adjacent) != 0 {
		t.Fatal("selection mutated reusable compiler", planner)
	}
	if _, method, reason := (&RoutineBuildingPlanner{goal: policy.EnsureBasicComfort, definition: "Table1x2c"}).selection(observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(3))}}); reason != "" || method != "basic-comfort-Table1x2c" {
		t.Fatal(method, reason)
	}
}
