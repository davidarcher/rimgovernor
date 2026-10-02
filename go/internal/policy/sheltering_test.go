package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// shelterFacts is a quiet colony with the Safe area "safe", two colonists
// ("a", "b") and a pet, all restricted to area.
func shelterFacts(area string) RoutineFacts {
	f := stableRoutine()
	f.ShelterArea = domain.Known("safe")
	f.DisasterConditions = domain.Known([]DisasterCondition{})
	f.Hostiles = domain.Known(int64(0))
	f.OutdoorTemperature, f.SleepingMin, f.SleepingMax = domain.Known(15.0), domain.Known(16.0), domain.Known(24.0)
	f.RecoverySafety = domain.Known(RecoverySafety{Restrictions: []RecoveryRestriction{{Pawn: "a", Area: domain.Known(area)}, {Pawn: "b", Area: domain.Known(area)}}})
	f.RecoveryWorkers = domain.Known([]RecoveryWorker{recoveryWorker("a"), recoveryWorker("b")})
	f.AnimalUpkeep.Animals = domain.Known([]UpkeepAnimal{{ID: "pet", Definition: "Husky", RequiresPen: domain.Known(false), SupportsAreas: domain.Known(true), AllowedArea: domain.Known(area), Release: domain.Known(false), Slaughter: domain.Known(false)}})
	return f
}

func everyoneTo(area string) []AllowedAreaChange {
	return []AllowedAreaChange{{Pawn: "a", Area: area}, {Pawn: "b", Area: area}, {Pawn: "pet", Animal: true, Area: area}}
}

func TestShelteringFalloutMovesEveryoneUndrafted(t *testing.T) {
	f := shelterFacts("")
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionToxicFallout}})
	if got := PlanSheltering(f); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatal(got)
	}
	workers, _ := f.RecoveryWorkers.Value()
	workers[1].Drafted = domain.Known(true)
	if got := PlanSheltering(f); len(got) != 2 || got[0].Pawn != "a" || got[1].Pawn != "pet" {
		t.Fatal("drafted pawn sheltered", got)
	}
	if !assessedDeficit(needs(t, f, RoutineLatches{}), RecoverDisasterServices) {
		t.Fatal("sheltering raised no incident")
	}
}

func TestShelteringTemperatureOnlyOutsideComfort(t *testing.T) {
	f := shelterFacts("")
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionColdSnap}})
	f.OutdoorTemperature = domain.Known(20.0)
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal("comfortable cold snap sheltered", got)
	}
	f.OutdoorTemperature = domain.Known(-12.0)
	if got := PlanSheltering(f); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatal(got)
	}
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionHeatWave}})
	f.OutdoorTemperature = domain.Known(45.0)
	if got := PlanSheltering(f); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatal(got)
	}
	// Cold outside without a cold snap is no trigger.
	f.DisasterConditions = domain.Known([]DisasterCondition{})
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal(got)
	}
}

func TestShelteringThreatSparesCombatants(t *testing.T) {
	f := shelterFacts("")
	f.Hostiles = domain.Known(int64(3))
	if got := PlanSheltering(f); len(got) != 1 || !got[0].Animal {
		t.Fatal("unknown draft set sheltered colonists", got)
	}
	f.ShelterCombatants = domain.Known([]PawnID{"a"})
	want := []AllowedAreaChange{{Pawn: "b", Area: "safe"}, {Pawn: "pet", Animal: true, Area: "safe"}}
	if got := PlanSheltering(f); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}

func TestShelteringRestoresOnlySafeWhenClear(t *testing.T) {
	if got := PlanSheltering(shelterFacts("safe")); !reflect.DeepEqual(got, everyoneTo("")) {
		t.Fatal(got)
	}
	if got := PlanSheltering(shelterFacts("other")); len(got) != 0 {
		t.Fatal("cleared another planner's area", got)
	}
	f := shelterFacts("safe")
	f.Hostiles = domain.Unknown[int64]()
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal("unknown trigger restored", got)
	}
}

func TestShelteringNeedsSafeAreaAndSkipsPenAnimals(t *testing.T) {
	f := shelterFacts("")
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionToxicFallout}})
	f.ShelterArea = domain.Known("")
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal("sheltered without a Safe area", got)
	}
	f.ShelterArea = domain.Known("safe")
	animals, _ := f.AnimalUpkeep.Animals.Value()
	animals[0].RequiresPen = domain.Known(true)
	if got := PlanSheltering(f); len(got) != 2 || got[1].Animal {
		t.Fatal("moved pen animal", got)
	}
}
