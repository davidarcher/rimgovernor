package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"math"
)

func validateMealRecipe(r *o.RecipeState) error {
	for _, n := range []*float64{r.Mood, r.NutrientEfficiency, r.WorkPerNutrition} {
		if n != nil && (math.IsNaN(*n) || math.IsInf(*n, 0)) {
			return contract("nonfinite meal recipe measure")
		}
	}
	if r.NutrientEfficiency != nil && r.GetNutrientEfficiency() <= 0 || r.GetWorkPerNutrition() < 0 {
		return contract("invalid meal recipe measure")
	}
	if len(r.Skills) > 64 {
		return contract("meal recipe skill bound")
	}
	seen := map[string]bool{}
	for _, s := range r.Skills {
		if s == nil || validID(s.GetDefName()) != nil || seen[s.GetDefName()] || s.Minimum == nil || s.GetMinimum() < 0 || s.GetMinimum() > 20 {
			return contract("invalid meal recipe skill")
		}
		seen[s.GetDefName()] = true
	}
	if r.IngredientClasses == nil {
		return nil
	}
	if len(r.IngredientClasses.Slots) == 0 || len(r.IngredientClasses.Slots) > 64 {
		return contract("invalid meal ingredient slots")
	}
	for _, slot := range r.IngredientClasses.Slots {
		if slot == nil || len(slot.Alternatives) == 0 || len(slot.Alternatives) > 3 {
			return contract("invalid meal ingredient alternatives")
		}
		classes := map[o.FoodIngredientClass]bool{}
		for _, c := range slot.Alternatives {
			if classes[c] || c < o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_MEAT || c > o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANY || c == o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANY && len(slot.Alternatives) != 1 {
				return contract("invalid meal ingredient class")
			}
			classes[c] = true
		}
	}
	return nil
}
