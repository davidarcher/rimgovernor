package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rearmWorker(id PawnID, available bool, hauling int, disabled bool) WorkPawn {
	return WorkPawn{ID: id, Available: domain.Known(available), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkConstruction, Priority: 1}, {Work: WorkHauling, Priority: hauling, Disabled: disabled}})}
}

func TestDefenseRearmTurretsCountsEmptyBarrelsAndOrdersOneRefuel(t *testing.T) {
	t.Parallel()
	a, b := domain.Cell{X: 10, Z: 10}, domain.Cell{X: 14, Z: 10}
	turrets := []DefenseTurretFacts{
		{ID: "T1", Cell: a, Powered: domain.Known(true), OutOfFuel: domain.Known(true), Fuel: domain.Known(0.0), TargetFuel: domain.Known(60.0), FuelDefinitions: []Resource{"Steel"}},
		{ID: "T2", Cell: b, Powered: domain.Known(false), OutOfFuel: domain.Known(true), Fuel: domain.Known(0.0), TargetFuel: domain.Known(60.0), FuelDefinitions: []Resource{"Steel"}},
	}
	workers := []WorkPawn{rearmWorker("drafted", false, 1, false), rearmWorker("nohaul", true, 0, true), rearmWorker("hauler", true, 3, false), rearmWorker("keen", true, 1, false)}
	got := DefenseRearmTurrets(turrets, workers, domain.Known(map[Resource]int64{"Steel": 120}), false)
	if !reflect.DeepEqual(got.Empty, []domain.Cell{a, b}) || !reflect.DeepEqual(got.Unpowered, []domain.Cell{b}) {
		t.Fatalf("%+v", got)
	}
	// Under a solar flare the dark turret is absent, not unpowered; the
	// empty barrels are still counted and rearmed for when it ends.
	if flare := DefenseRearmTurrets(turrets, workers, domain.Known(map[Resource]int64{"Steel": 120}), true); len(flare.Unpowered) != 0 || !reflect.DeepEqual(flare.Empty, got.Empty) || !reflect.DeepEqual(flare.Rearm, got.Rearm) {
		t.Fatalf("%+v", flare)
	}
	// One order per step, for the first empty barrel, carried by the
	// highest-priority enabled hauler; nothing is short.
	if !reflect.DeepEqual(got.Rearm, []DefenseRearm{{Turret: "T1", Cell: a, Pawn: "keen", Fuel: "Steel"}}) || len(got.Shortage) != 0 {
		t.Fatalf("%+v", got)
	}
	// Hauling at priority 0 still carries a forced order when nobody has it
	// enabled; a disabled work type never does.
	got = DefenseRearmTurrets(turrets, []WorkPawn{rearmWorker("nohaul", true, 0, true), rearmWorker("idle", true, 0, false)}, domain.Known(map[Resource]int64{"Steel": 1}), false)
	if len(got.Rearm) != 1 || got.Rearm[0].Pawn != "idle" {
		t.Fatalf("%+v", got)
	}
	if got = DefenseRearmTurrets(turrets, []WorkPawn{rearmWorker("nohaul", true, 0, true)}, domain.Known(map[Resource]int64{"Steel": 1}), false); len(got.Rearm) != 0 || len(got.Empty) != 2 {
		t.Fatalf("%+v", got)
	}
}

func TestDefenseRearmTurretsReportsShortageAndUnknowns(t *testing.T) {
	t.Parallel()
	a := domain.Cell{X: 10, Z: 10}
	empty := DefenseTurretFacts{ID: "T1", Cell: a, Powered: domain.Known(true), OutOfFuel: domain.Known(true), Fuel: domain.Known(12.5), TargetFuel: domain.Known(60.0), FuelDefinitions: []Resource{"Steel"}}
	workers := []WorkPawn{rearmWorker("hauler", true, 3, false)}
	// No steel: the barrel's gap is the shortage and no order is proposed.
	got := DefenseRearmTurrets([]DefenseTurretFacts{empty, empty}, workers, domain.Known(map[Resource]int64{"WoodLog": 500}), false)
	if len(got.Rearm) != 0 || !reflect.DeepEqual(got.Shortage, []Amount{{Resource: "Steel", Count: 96}}) {
		t.Fatalf("%+v", got)
	}
	// Unknown stock: the deficit stands, but neither an order nor a
	// shortage can be derived from it.
	got = DefenseRearmTurrets([]DefenseTurretFacts{empty}, workers, domain.Unknown[map[Resource]int64](), false)
	if len(got.Empty) != 1 || len(got.Rearm) != 0 || len(got.Shortage) != 0 {
		t.Fatalf("%+v", got)
	}
	// Unknown fuel state or a full barrel is no deficit; unknown levels on
	// an empty barrel still count one unit short.
	full := empty
	full.OutOfFuel = domain.Known(false)
	unknown := empty
	unknown.OutOfFuel = domain.Unknown[bool]()
	levels := empty
	levels.Fuel, levels.TargetFuel = domain.Unknown[float64](), domain.Unknown[float64]()
	got = DefenseRearmTurrets([]DefenseTurretFacts{full, unknown, levels}, workers, domain.Known(map[Resource]int64{}), false)
	if !reflect.DeepEqual(got.Empty, []domain.Cell{a}) || !reflect.DeepEqual(got.Shortage, []Amount{{Resource: "Steel", Count: 1}}) {
		t.Fatalf("%+v", got)
	}
	// A turret without an identity is a deficit that cannot be ordered.
	anonymous := empty
	anonymous.ID = ""
	if got = DefenseRearmTurrets([]DefenseTurretFacts{anonymous}, workers, domain.Known(map[Resource]int64{"Steel": 9}), false); len(got.Rearm) != 0 || len(got.Empty) != 1 {
		t.Fatalf("%+v", got)
	}
}

func TestResourceGoalTargetsMergesDerivedFloors(t *testing.T) {
	t.Parallel()
	configured := map[Resource]int64{"WoodLog": 200, "Steel": 30}
	if got := ResourceGoalTargets(configured, nil); !reflect.DeepEqual(got, configured) {
		t.Fatal(got)
	}
	got := ResourceGoalTargets(configured, map[Resource]int64{"Steel": 60, "WoodLog": 10, "ComponentIndustrial": 0, "Plasteel": 20000})
	want := map[Resource]int64{"WoodLog": 200, "Steel": 60}
	if !reflect.DeepEqual(got, want) || configured["Steel"] != 30 {
		t.Fatalf("%v (configured %v)", got, configured)
	}
	if got := ResourceGoalTargets(nil, map[Resource]int64{"Steel": 60}); !reflect.DeepEqual(got, map[Resource]int64{"Steel": 60}) {
		t.Fatal(got)
	}
}
