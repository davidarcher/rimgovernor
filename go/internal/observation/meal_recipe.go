package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"math"
)

func mealRecipeFacts(r *o.RecipeState, recipe *policy.ProductionRecipe) {
	recipe.Mood = finiteRecipeNumber(r.Mood, false)
	recipe.NutrientEfficiency = finiteRecipeNumber(r.NutrientEfficiency, true)
	recipe.WorkPerNutrition = finiteRecipeNumber(r.WorkPerNutrition, true)
	recipe.NeedsPower = optional(r.NeedsPower)
	classes := r.IngredientClasses
	if classes == nil || len(classes.Slots) == 0 || len(classes.Slots) > 64 {
		return
	}
	slots := make([]policy.FoodIngredientSlot, 0, len(classes.Slots))
	for _, slot := range classes.Slots {
		if slot == nil || len(slot.Alternatives) == 0 || len(slot.Alternatives) > 3 {
			return
		}
		out := policy.FoodIngredientSlot{}
		seen := map[o.FoodIngredientClass]bool{}
		for _, kind := range slot.Alternatives {
			if seen[kind] {
				return
			}
			seen[kind] = true
			var value policy.FoodIngredientClass
			switch kind {
			case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_MEAT:
				value = policy.IngredientMeat
			case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_VEGETABLE:
				value = policy.IngredientVegetable
			case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANIMAL_PRODUCT:
				value = policy.IngredientAnimalProduct
			case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANY:
				if len(slot.Alternatives) != 1 {
					return
				}
				value = policy.IngredientAny
			default:
				return
			}
			out.Alternatives = append(out.Alternatives, value)
		}
		slots = append(slots, out)
	}
	recipe.IngredientClasses = domain.Known(slots)
	floor := int32(0)
	for _, skill := range r.Skills {
		if skill.GetDefName() != "Cooking" {
			continue
		}
		if skill.Minimum == nil || skill.GetMinimum() < 0 || skill.GetMinimum() > 20 {
			return
		}
		if skill.GetMinimum() > floor {
			floor = skill.GetMinimum()
		}
	}
	recipe.CookSkillFloor = domain.Known(floor)
}

func finiteRecipeNumber(value *float64, nonnegative bool) domain.Fact[float64] {
	if value == nil || math.IsNaN(*value) || math.IsInf(*value, 0) || nonnegative && *value < 0 {
		return domain.Unknown[float64]()
	}
	return domain.Known(*value)
}
