package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestReserveRefillIndependentOfFoodDeficit(t *testing.T) {
	f := stableRoutine()
	f.FoodReserve = domain.Known(FoodReserveReview{TargetNutrition: 5, DeficitNutrition: 5})
	r := needs(t, f, RoutineLatches{})
	if hasNeed(r, EnsureFoodSupply) || !hasNeed(r, MaintainFoodStorage) {
		t.Fatal(r.Goals)
	}
	for _, goal := range r.Goals {
		if goal.ID == MaintainFoodStorage && goal.MethodUnavailable {
			t.Fatal(goal)
		}
	}
	f.FoodReserve = domain.Known(FoodReserveReview{TargetNutrition: 5, StockNutrition: 5})
	if hasNeed(needs(t, f, RoutineLatches{}), MaintainFoodStorage) {
		t.Fatal("filled reserve remains in deficit")
	}
}

func TestReserveReleaseBypassesDevelopmentQueue(t *testing.T) {
	f := stableRoutine()
	f.FoodReserve = domain.Known(FoodReserveReview{Emergency: true, Release: []string{"reserve"}})
	for _, goal := range needs(t, f, RoutineLatches{}).Goals {
		if goal.ID == MaintainFoodStorage {
			if goal.Priority != 2 || goal.MethodUnavailable {
				t.Fatal(goal)
			}
			return
		}
	}
	t.Fatal("release goal absent")
}

func TestReserveRefillRanksADevelopmentDeficit(t *testing.T) {
	f := stableRoutine()
	f.FoodReserve = domain.Known(FoodReserveReview{TargetNutrition: 10, StockNutrition: 4, DeficitNutrition: 6})
	if d, known := RoutineDevelopmentDeficit(MaintainFoodStorage, f, DefaultRoutinePolicy()).Value(); !known || d != 0.6 {
		t.Fatal("refill must rank for a slot", d, known)
	}
	f.FoodReserve = domain.Known(FoodReserveReview{TargetNutrition: 10, StockNutrition: 10, Hold: []string{"stack"}})
	if d, known := RoutineDevelopmentDeficit(MaintainFoodStorage, f, DefaultRoutinePolicy()).Value(); !known || d != 1 {
		t.Fatal("pending access is a full deficit", d, known)
	}
	f.FoodReserve = domain.Unknown[FoodReserveReview]()
	if _, known := RoutineDevelopmentDeficit(MaintainFoodStorage, f, DefaultRoutinePolicy()).Value(); known {
		t.Fatal("unknown review ranks unknown")
	}
}
