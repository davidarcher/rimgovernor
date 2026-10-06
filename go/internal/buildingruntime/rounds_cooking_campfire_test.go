package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func campfireFacts(benches []observation.CookingBench, buildings ...policy.CurrentBuilding) observation.ColonyProjection {
	var facts observation.ColonyProjection
	facts.CookingBenches = domain.Known(benches)
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{
		{ID: "shell", Role: domain.Known(policy.RoomRoleBarracks), Beds: []string{"spot1"}, Cells: []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}},
		{ID: "kitchen", Role: domain.Known(policy.RoomRoleKitchen), Cells: []domain.Cell{{X: 9, Z: 9}}},
	}})
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: buildings})
	return facts
}

func campfireBuilding(t *testing.T, id string, cell domain.Cell) policy.CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding("Campfire", cell, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	return policy.CurrentBuilding{ID: id, Building: b, Cells: []domain.Cell{cell}}
}

func TestCampfireInSleepingRoomIsNotRetired(t *testing.T) {
	fire := campfireBuilding(t, "fire1", domain.Cell{X: 1, Z: 1})
	facts := campfireFacts([]observation.CookingBench{{ID: "fire1", Definition: "Campfire", Usable: domain.Known(true), Room: domain.Known("shell")}}, fire)
	if _, owed := campfireRetirement(facts, nil); owed {
		t.Fatal("a campfire in a sleeping room was retired with no stove kitchen")
	}
}

func TestOutdoorCampfireStaysUntilAStoveKitchen(t *testing.T) {
	fire := campfireBuilding(t, "fire1", domain.Cell{X: 20, Z: 20})
	outdoor := observation.CookingBench{ID: "fire1", Definition: "Campfire", Usable: domain.Known(true)}
	if _, owed := campfireRetirement(campfireFacts([]observation.CookingBench{outdoor}, fire), nil); owed {
		t.Fatal("outdoor campfire retired with no stove")
	}
	stove := observation.CookingBench{ID: "stove1", Definition: "FueledStove", Usable: domain.Known(true), Room: domain.Known("kitchen")}
	facts := campfireFacts([]observation.CookingBench{outdoor, stove}, fire)
	got, owed := campfireRetirement(facts, nil)
	if !owed || got.ID != "fire1" {
		t.Fatalf("retirement = %v %v, want fire1 once a stove kitchen stands", got, owed)
	}
	heat := []policy.ConstructionClaim{{Concern: policy.EnsureTemperatureSafety, Building: fire.Building, Cells: fire.Cells}}
	if _, owed := campfireRetirement(facts, heat); owed {
		t.Fatal("a heat campfire the temperature family claimed was retired")
	}
	value, err := domain.NewDeconstruction(got.ID, got.Building.Definition(), got.Cells[0])
	if err != nil {
		t.Fatalf("deconstruct plan: %v", err)
	}
	if _, err := domain.NewDeconstructionAction("p-0", value); err != nil {
		t.Fatalf("deconstruct action: %v", err)
	}
	if owed := campfireRetireOwed(facts); !positiveFact(owed) {
		t.Fatalf("CampfireRetireOwed = %v, want true", owed)
	}
}

// A heat campfire (#1180) is the claimed one; its warm sleeping room owes
// the refuel switch, which the review reads as TemperatureOwed.
func TestHeatTemperatureOwed(t *testing.T) {
	fire := campfireBuilding(t, "fire1", domain.Cell{X: 1, Z: 1})
	facts := campfireFacts([]observation.CookingBench{{ID: "fire1", Definition: "Campfire", Usable: domain.Known(true), Room: domain.Known("shell"), AutoRefuel: domain.Known(true)}}, fire)
	rooms, _ := facts.Rooms.Value()
	rooms.Rooms[0].Temperature = domain.Known(27.0)
	facts.Rooms = domain.Known(rooms)
	// The sleeper's own comfortable range is the band the refuel switch follows.
	facts.Facts.Sleeping = domain.Known(policy.SleepingObservation{People: []policy.SleepingPerson{{ID: "pawn", OwnedBed: domain.Known("spot1"), ComfortableMin: domain.Known(16.0), ComfortableMax: domain.Known(26.0)}}})
	if got := heatCampfires(facts); len(got) != 0 || positiveFact(temperatureOwed(facts)) {
		t.Fatalf("an unclaimed campfire counted as heat: %v", got)
	}
	facts.Facts.ConstructionClaims = domain.Known([]policy.ConstructionClaim{{Concern: policy.EnsureTemperatureSafety, Building: fire.Building, Cells: fire.Cells}})
	if got := heatCampfires(facts); len(got) != 1 || got[0].ID != "fire1" || !positiveFact(temperatureOwed(facts)) {
		t.Fatalf("heat campfires = %v, want fire1 owing its refuel switch", got)
	}
	proposal, owed := policy.CampfireRefuel(facts.Rooms, temperatureCooling(facts))
	value, err := domain.NewAutoRefuel(proposal.Thing, proposal.Method == policy.TemperatureRefuelOn)
	if !owed || err != nil || value.Allow() {
		t.Fatalf("refuel = %+v %v %v, want off", proposal, value, err)
	}
	if _, err := domain.NewAutoRefuelAction("p-0", value); err != nil {
		t.Fatal(err)
	}
}

// A campfire blueprint from an earlier (retired) cooking plan still standing
// is the camp's campfire: the planner stages no other (#1534).
func TestCookingSelectionCountsStandingCampfireBlueprint(t *testing.T) {
	t.Parallel()
	building, err := domain.NewBuilding("Campfire", domain.Cell{X: 10, Z: 10}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	var facts observation.ColonyProjection
	facts.Facts.FoodPlan = domain.Known(policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Channel: policy.SupplyCandidate{Kind: policy.CandidateCook, ID: "cooking-capacity"}, Decision: policy.FoodPlanOpen}}})
	facts.Facts.Colonists = domain.Known(int64(3))
	facts.Facts.Cooking = domain.Known(false)
	facts.CookingBenches = domain.Known([]observation.CookingBench{})
	facts.Facts.ConstructionClaims = domain.Known([]policy.ConstructionClaim{{Plan: "p1", Action: "p1-0", Concern: "g", Building: building}})
	r := &RoundsBuildingPlanner{concern: policy.EnsureCooking, definition: "Campfire"}

	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	if n, method, reason := r.selection(facts); n != 1 || method != "campfire" {
		t.Fatal("no campfire standing:", n, method, reason)
	}
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Sites: []policy.ConstructionSite{{Building: building, Stage: "blueprint"}}})
	if n, _, reason := r.selection(facts); n != 0 || reason != BuildingExistingFacility {
		t.Fatal("standing blueprint:", n, reason)
	}
}

// A built campfire the cooking census omits (walled in before its door, so
// unreachable) is still the camp's campfire: no second one is staged.
func TestCookingSelectionCountsBuiltCampfireMissingFromCensus(t *testing.T) {
	t.Parallel()
	fire := campfireBuilding(t, "fire1", domain.Cell{X: 10, Z: 10})
	var facts observation.ColonyProjection
	facts.Facts.FoodPlan = domain.Known(policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Channel: policy.SupplyCandidate{Kind: policy.CandidateCook, ID: "cooking-capacity"}, Decision: policy.FoodPlanOpen}}})
	facts.Facts.Colonists = domain.Known(int64(3))
	facts.Facts.Cooking = domain.Known(false)
	facts.CookingBenches = domain.Known([]observation.CookingBench{})
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{fire}})
	r := &RoundsBuildingPlanner{concern: policy.EnsureCooking, definition: "Campfire"}
	if n, _, reason := r.selection(facts); n != 0 || reason != BuildingExistingFacility {
		t.Fatal("built campfire missing from the census:", n, reason)
	}
}
