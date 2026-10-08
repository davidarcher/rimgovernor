package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestReserveRefillIndependentOfFoodDeficit(t *testing.T) {
	f := stableRounds()
	f.FoodReserve = domain.Known(FoodReserveReview{TargetNutrition: 5, DeficitNutrition: 5})
	r := needs(t, f, RoundsLatches{})
	if hasNeed(r, EnsureFoodSupply) || !hasNeed(r, MaintainFoodStorage) {
		t.Fatal(r.Concerns)
	}
	for _, goal := range r.Concerns {
		if goal.ID == MaintainFoodStorage && goal.MethodUnavailable {
			t.Fatal(goal)
		}
	}
	f.FoodReserve = domain.Known(FoodReserveReview{TargetNutrition: 5, StockNutrition: 5})
	if hasNeed(needs(t, f, RoundsLatches{}), MaintainFoodStorage) {
		t.Fatal("filled reserve remains in deficit")
	}
}

func TestReserveReleaseBypassesDevelopmentQueue(t *testing.T) {
	f := stableRounds()
	f.FoodReserve = domain.Known(FoodReserveReview{Emergency: true, Release: []string{"reserve"}})
	for _, goal := range needs(t, f, RoundsLatches{}).Concerns {
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
	f := stableRounds()
	f.FoodReserve = domain.Known(FoodReserveReview{TargetNutrition: 10, StockNutrition: 4, DeficitNutrition: 6})
	if d, known := RoundsDeficit(MaintainFoodStorage, f, DefaultRoundsPolicy()).Value(); !known || d != 0.6 {
		t.Fatal("refill must rank for a slot", d, known)
	}
	f.FoodReserve = domain.Known(FoodReserveReview{TargetNutrition: 10, StockNutrition: 10, Hold: []string{"stack"}})
	if d, known := RoundsDeficit(MaintainFoodStorage, f, DefaultRoundsPolicy()).Value(); !known || d != 1 {
		t.Fatal("pending access is a full deficit", d, known)
	}
	f.FoodReserve = domain.Unknown[FoodReserveReview]()
	if _, known := RoundsDeficit(MaintainFoodStorage, f, DefaultRoundsPolicy()).Value(); known {
		t.Fatal("unknown review ranks unknown")
	}
}
