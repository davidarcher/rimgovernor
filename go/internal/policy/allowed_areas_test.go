package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func areaFacts(hazard bool, area string) RoutineFacts {
	f := stableRoutine()
	f.RecoverySafety = domain.Known(RecoverySafety{RoofHazard: domain.Known(hazard), SafeAreas: []string{"refuge"}, Restrictions: []RecoveryRestriction{{Pawn: "colonist", Area: domain.Known(area)}}})
	f.RecoveryWorkers = domain.Known([]RecoveryWorker{recoveryWorker("colonist")})
	f.AnimalUpkeep.Animals = domain.Known([]UpkeepAnimal{{ID: "pet", Definition: "Husky", RequiresPen: domain.Known(false), SupportsAreas: domain.Known(true), AllowedArea: domain.Known(area), Release: domain.Known(false), Slaughter: domain.Known(false)}})
	return f
}

func TestAllowedAreasHazardThenClearWithoutDisasterHistory(t *testing.T) {
	f := areaFacts(true, "manual")
	want := []AllowedAreaChange{{Pawn: "colonist", Area: "refuge"}, {Pawn: "pet", Animal: true, Area: "refuge"}}
	if got := PlanAllowedAreas(f); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	if got := PlanAllowedAreas(areaFacts(true, "refuge")); len(got) != 0 {
		t.Fatal(got)
	}
	want[0].Area, want[1].Area = "", ""
	for _, area := range []string{"refuge", "manual-excludes-food-and-work"} {
		f = areaFacts(false, area)
		if got := PlanAllowedAreas(f); !reflect.DeepEqual(got, want) {
			t.Fatal(got)
		}
		if !hasNeed(needs(t, f, RoutineLatches{}), RecoverDisasterServices) {
			t.Fatal("no correction goal without disaster history")
		}
	}
	if got := PlanAllowedAreas(areaFacts(false, "")); len(got) != 0 {
		t.Fatal(got)
	}
	if hasNeed(needs(t, areaFacts(false, ""), RoutineLatches{}), RecoverDisasterServices) {
		t.Fatal("corrected area remained deficient")
	}
}

func TestAllowedAreasUnknownHazardAndPenAnimals(t *testing.T) {
	f := areaFacts(false, "manual")
	safety, _ := f.RecoverySafety.Value()
	safety.RoofHazard = domain.Unknown[bool]()
	f.RecoverySafety = domain.Known(safety)
	if got := PlanAllowedAreas(f); len(got) != 0 {
		t.Fatal("unknown hazard widened access", got)
	}
	f = areaFacts(true, "manual")
	safety, _ = f.RecoverySafety.Value()
	safety.SafeAreas = nil
	f.RecoverySafety = domain.Known(safety)
	if got := PlanAllowedAreas(f); len(got) != 0 {
		t.Fatal("hazard without refuge widened access", got)
	}
	f = areaFacts(false, "manual")
	animals, _ := f.AnimalUpkeep.Animals.Value()
	animals[0].RequiresPen = domain.Known(true)
	f.AnimalUpkeep.Animals = domain.Known(animals)
	if got := PlanAllowedAreas(f); len(got) != 1 || got[0].Animal {
		t.Fatal("changed pen animal", got)
	}
}

func TestAllowedAreasRenewedRestrictionAndReload(t *testing.T) {
	// No controller latch or saved origin is required: this is the same census
	// a new controller sees after loading the saved native restriction.
	for range 3 {
		if len(PlanAllowedAreas(areaFacts(false, "manual"))) != 2 {
			t.Fatal("lost renewed restriction")
		}
		if len(PlanAllowedAreas(areaFacts(false, ""))) != 0 {
			t.Fatal("repeated corrected write")
		}
	}
}
