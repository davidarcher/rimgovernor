package bills

import (
	"fmt"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"math"
)

// Core-only definition expectations catch category mistakes such as treating
// milk's Fluid food-type flag as an unknown ingredient instead of animal product.
func checkMealFacts(recipe map[string]any) error {
	definition, _ := na.AsMap(recipe["recipe"])
	name := na.AsString(definition["defName"])
	mood, efficiency, slots := 0.0, 1.8, [][]string{{"FOOD_INGREDIENT_CLASS_ANY"}}
	switch name {
	case "CookMealSimple", "CookMealSimpleBulk":
	case "CookMealFine", "CookMealFineBulk":
		mood = 5
		slots = [][]string{{"FOOD_INGREDIENT_CLASS_MEAT", "FOOD_INGREDIENT_CLASS_ANIMAL_PRODUCT"}, {"FOOD_INGREDIENT_CLASS_VEGETABLE"}}
	case "CookMealLavish", "CookMealLavishBulk":
		mood, efficiency = 12, 1
		slots = [][]string{{"FOOD_INGREDIENT_CLASS_MEAT", "FOOD_INGREDIENT_CLASS_ANIMAL_PRODUCT"}, {"FOOD_INGREDIENT_CLASS_VEGETABLE"}}
	default:
		return nil
	}
	for key, expected := range map[string]float64{"mood": mood, "nutrientEfficiency": efficiency} {
		value, exists := recipe[key]
		if !exists || math.Abs(na.AsNumber(value)-expected) > 1e-5 {
			return fmt.Errorf("%s: %s = %v, want %v", name, key, value, expected)
		}
	}
	if _, present := recipe["needsPower"]; !present || na.AsNumber(recipe["workPerNutrition"]) <= 0 {
		return fmt.Errorf("%s: missing power/work facts", name)
	}
	classes, _ := na.AsMap(recipe["ingredientClasses"])
	actual := na.AsSlice(classes["slots"])
	if len(actual) != len(slots) {
		return fmt.Errorf("%s: ingredient slots = %v, want %v", name, actual, slots)
	}
	for i, raw := range actual {
		slot, _ := na.AsMap(raw)
		alternatives := na.AsSlice(slot["alternatives"])
		if len(alternatives) != len(slots[i]) {
			return fmt.Errorf("%s: ingredient alternatives = %v, want %v", name, alternatives, slots[i])
		}
		for j, value := range alternatives {
			if na.AsString(value) != slots[i][j] {
				return fmt.Errorf("%s: ingredient alternatives = %v, want %v", name, alternatives, slots[i])
			}
		}
	}
	return nil
}
