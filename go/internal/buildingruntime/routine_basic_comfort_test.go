package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRecreationVarietyUsesComfortLayoutAndPowerAtSite(t *testing.T) {
	people := []policy.PawnID{"a", "b"}
	v := policy.ComfortObservation{People: people, Dining: []policy.ComfortFacility{{ID: "chair", AccessibleTo: people}}, Recreation: []policy.ComfortFacility{{ID: "pin", Kind: "Dexterity", AccessibleTo: people}}, Joy: &policy.RecreationCensus{Kinds: []string{"Dexterity"}, Methods: []policy.JoyBuildingMethod{{Definition: "TubeTelevision", Kind: "Television", PowerW: 100}, {Definition: "BilliardsTable", Kind: "Dexterity"}, {Definition: "ChessTable", Kind: "Cerebral"}}}}
	for _, id := range people {
		v.Joy.Pawns = append(v.Joy.Pawns, policy.JoyTolerance{Pawn: id, Tolerance: []float64{0}, Bored: []bool{false}})
	}
	f := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known[int64](2), BasicComfort: domain.Known(v)}, WorkPawns: domain.Known([]policy.WorkPawn{{ID: "a", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]policy.WorkPriority{{Work: "Construction", Priority: 3}})}})}
	for _, name := range policy.RecreationDefinitions {
		f.Definitions = append(f.Definitions, observation.PlanningDefinition{Name: name, Available: domain.Known(true), ConstructionSkill: domain.Known[int32](0), Stuff: domain.Known("WoodLog")})
	}
	planner := &RoutineBuildingPlanner{goal: policy.EnsureBasicComfort}
	check := func(want string) {
		t.Helper()
		selected, reason, err := planner.selectBasicComfort(f)
		if err != nil || reason != "" || selected == nil || selected.definition != want || selected.environment != policy.PlacementIndoors || selected.facility != nil {
			t.Fatal(selected, reason, err)
		}
		_, key, _ := selected.selection(f)
		if key != domain.MethodID("basic-comfort-"+want) {
			t.Fatal(key)
		}
	}
	check("ChessTable") // Unknown power and same-kind billiards both skipped.
	cell := domain.Cell{X: 12, Z: 10}
	f.Cells = []policy.SiteCell{{Cell: cell, Roofed: domain.Known(true), Indoors: domain.Known(true), Occupied: domain.Known(false)}}
	topology := policy.PowerTopology{Blackout: domain.Known(false), Buildings: []policy.PowerSite{{Cell: domain.Cell{X: 10, Z: 10}, PowerBuilding: policy.PowerBuilding{OutputW: domain.Known(1000.0), Connected: domain.Known(true), Network: domain.Known("grid")}}}, Networks: []policy.PowerNetworkFact{{ID: "grid", GenerationW: domain.Known(1000.0), ConsumptionW: domain.Known(500.0)}}}
	f.PowerPlanning = domain.Known(topology)
	check("TubeTelevision")
	if poweredRecreationCell(f, domain.Cell{X: 30, Z: 30}, 100) {
		t.Fatal("remote generator powered TV site")
	}
	topology.Networks[0].ID = "other-grid"
	f.PowerPlanning = domain.Known(topology)
	check("ChessTable")
	topology.Networks[0].ID = "grid"
	topology.Networks[0].ConsumptionW = domain.Known(950.0)
	f.PowerPlanning = domain.Known(topology)
	check("ChessTable")
	// Research/definition availability remains native even if the candidate
	// list and planning definition changed across projections.
	f.Definitions[2].Available = domain.Known(false)
	selected, reason, err := planner.selectBasicComfort(f)
	if err != nil || selected != nil || reason != BuildingExistingFacility {
		t.Fatal(selected, reason, err)
	}
}

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
	// A reachable, unused pin with an unbored lone colonist is provided.
	census.Recreation = []policy.ComfortFacility{{ID: "pin", Kind: "Dexterity", AccessibleTo: people}}
	census.Joy = &policy.RecreationCensus{Kinds: []string{"Dexterity"}, Pawns: []policy.JoyTolerance{{Pawn: "pawn", Tolerance: []float64{0}, Bored: []bool{false}}}}
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
