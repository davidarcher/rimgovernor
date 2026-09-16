package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func TestRoutineDisabledMethodsYieldSlotsWithoutErasingNeeds(t *testing.T) {
	f := stableRoutine()
	f.IndoorCapacity = domain.Known(int64(3))
	f.ComfortRecovered = domain.Known(false)
	f.ComfortDeficit = domain.Known(.8)
	f.AvailableMethods = domain.Known([]GoalID{EnsureExpansion})
	needs := needs(t, f, RoutineLatches{})
	request := developmentFixture()
	request.Goals = needs.Goals
	got := rank(t, request)
	if !reflect.DeepEqual(selected(got), []GoalID{EnsureExpansion}) {
		t.Fatal(got)
	}
	found := false
	for _, a := range needs.Assessments {
		if a.ID == EnsureComfort {
			found = true
			if a.Need != domain.NeedDeficit {
				t.Fatal(a)
			}
		}
	}
	if !found {
		t.Fatal("disabled method erased need")
	}
	for _, row := range got.Rows {
		if row.Goal == EnsureComfort && row.Reason != DevelopmentMethodUnavailable {
			t.Fatal(row)
		}
	}
	f.AvailableMethods = domain.Known([]GoalID{})
	empty, err := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	request.Goals = empty.Goals
	if len(selected(rank(t, request))) != 0 {
		t.Fatal("disabled methods admitted")
	}
	for _, bad := range [][]GoalID{{EnsureComfort, EnsureComfort}, {"unknown-method"}} {
		f.AvailableMethods = domain.Known(bad)
		if _, err := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy()); err == nil {
			t.Fatal(bad)
		}
	}
}

// Every method capability the composed serve default declares must validate
// against empty facts, which is how NewRoutineReviewer checks them before any
// native read. RecoverDisasterServices is only assessed once a disaster
// history exists, so it needs an explicit recognition.
func TestRoutineComposedCapabilitiesValidateOnEmptyFacts(t *testing.T) {
	all := []GoalID{EnsureFoodSupply, EnsureFoodStorage, MaintainWood, EnsureCooking, EnsureTemperatureSafety, EnsureBasicPower, EnsureComfort, EnsureExpansion, MaintainAnimalContainment, MaintainEssentialRepairs, MaintainCleanFacilities, MaintainStorage, MaintainWaste, RecoverDisasterServices, MaintainHerd, MaintainPopulation, MaintainHomeCoverage, MaintainStoneShell, EnsureResearch, MaintainResource, MaintainAnimalFeed, ProductionPolicy}
	if _, err := DetectRoutine(RoutineFacts{AvailableMethods: domain.Known(all)}, RoutineLatches{}, DefaultRoutinePolicy()); err != nil {
		t.Fatal(err)
	}
}
