package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func assessment(t *testing.T, r RoutineNeeds, id GoalID) domain.NeedState {
	t.Helper()
	for _, n := range r.Assessments {
		if n.ID == id {
			return n.Need
		}
	}
	t.Fatal("missing assessment", id)
	return ""
}

func TestRoutineAssessmentsDoNotInferRecoveryFromAbsentWork(t *testing.T) {
	r := needs(t, RoutineFacts{}, RoutineLatches{})
	if len(r.Assessments) != 21 {
		t.Fatal(r)
	}
	for _, n := range r.Assessments {
		if n.Need != domain.NeedUnknown {
			t.Fatal("missing native facts became evidence", n)
		}
	}
	r = needs(t, stableRoutine(), RoutineLatches{})
	for _, n := range r.Assessments {
		if n.Need != domain.NeedRecovered {
			t.Fatal("stable evidence not recovered", n)
		}
	}
}

func TestRoutineAssessmentsRetainHysteresisButRequireFreshEvidence(t *testing.T) {
	f := stableRoutine()
	f.FoodDays = domain.Known(2.0)
	r := needs(t, f, RoutineLatches{})
	f.FoodDays = domain.Known(5.0)
	r = needs(t, f, r.Latches)
	if assessment(t, r, EnsureFoodSupply) != domain.NeedDeficit {
		t.Fatal(r)
	}
	f.FoodDays = domain.Unknown[float64]()
	r = needs(t, f, r.Latches)
	if !r.Latches.Food || assessment(t, r, EnsureFoodSupply) != domain.NeedUnknown {
		t.Fatal(r)
	}
	f.FoodDays = domain.Known(8.0)
	r = needs(t, f, r.Latches)
	if assessment(t, r, EnsureFoodSupply) != domain.NeedRecovered {
		t.Fatal(r)
	}
	f.BedCapacity = domain.Known(int64(0))
	f.IndoorCapacity = domain.Unknown[int64]()
	r = needs(t, f, r.Latches)
	if assessment(t, r, EnsureInitialShelter) != domain.NeedDeficit {
		t.Fatal("known shortage masked by unknown neighbor", r)
	}
}
