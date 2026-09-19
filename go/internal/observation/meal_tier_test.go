package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestMealRequestExcludesMealsAndUnallocatedCooks(t *testing.T) {
	p := ColonyProjection{CombinedFoodSupply: domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "p", NutritionPerDay: domain.Known(1.0)}}, Stocks: []policy.FoodStock{
		{ID: "raw", Holder: domain.Known(policy.PawnID("")), Nutrition: domain.Known(8.0), Eaters: []policy.PawnID{"p"}, Perishable: domain.Known(false), RawClass: domain.Known(policy.IngredientVegetable)},
		{ID: "meal", Holder: domain.Known(policy.PawnID("")), Nutrition: domain.Known(100.0), Eaters: []policy.PawnID{"p"}, Perishable: domain.Known(false), RawClass: domain.Known(policy.FoodIngredientClass(""))},
	}}), WorkPawns: domain.Known([]policy.WorkPawn{{ID: "p", Available: domain.Known(true), Skills: domain.Known([]policy.WorkSkill{{Name: "Cooking", Level: 12}}), Work: domain.Known([]policy.WorkPriority{{Work: policy.WorkCooking, Priority: 0}})}})}
	r := p.MealRequest(3, 7)
	days, known := r.RawRunwayDays.Value()
	cooks, ck := r.Cooks.Value()
	if !known || days != 8 || !ck || len(cooks) != 0 {
		t.Fatal(r)
	}
	pawns, _ := p.WorkPawns.Value()
	pawns[0].Work = domain.Known([]policy.WorkPriority{{Work: policy.WorkCooking, Priority: 1}})
	p.WorkPawns = domain.Known(pawns)
	cooks, ck = p.MealRequest(3, 7).Cooks.Value()
	if !ck || len(cooks) != 1 || cooks[0].Skill != 12 {
		t.Fatal(cooks, ck)
	}
}
