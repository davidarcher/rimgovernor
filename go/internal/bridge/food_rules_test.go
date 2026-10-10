package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestFoodRulesReadTheDefRows is the parity check against the native food
// census these rules replaced (NativeFoodPolicy.Kind, FoodUtility.GetFoodKind,
// ThingDef.IsNutritionGivingIngestible/IsDrug/IsCorpse): over the recorded
// vanilla defs each food has the kind native gave it, and a drug and a corpse
// are no food.
func TestFoodRulesReadTheDefRows(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	kinds := map[string]policy.FoodKind{
		"MealSimple": policy.FoodKindMealSimple, "MealFine": policy.FoodKindMealFine, "MealLavish": policy.FoodKindMealLavish,
		"MealNutrientPaste": policy.FoodKindMealAwful, "MealSurvivalPack": policy.FoodKindMealSimple,
		"Meat_Muffalo": policy.FoodKindRawMeat, "Meat_Human": policy.FoodKindHumanMeat, "Meat_Megaspider": policy.FoodKindInsectMeat,
		"RawPotatoes": policy.FoodKindVegetable, "RawBerries": policy.FoodKindVegetable, "RawFungus": policy.FoodKindFungus,
		"Milk": policy.FoodKindAnimalProduct, "EggChickenUnfertilized": policy.FoodKindAnimalProduct,
		"Kibble": policy.FoodKindKibble, "Hay": policy.FoodKindHay, "Pemmican": policy.FoodKindMealSimple,
	}
	for name, want := range kinds {
		got, err := catalog.FoodKindOf(name)
		if err != nil || got != want {
			t.Errorf("%s is %q (%v), want %q", name, got, err, want)
		}
	}
	for _, name := range []string{"Beer", "Neutroamine", "Penoxycyline", "Corpse_Human", "Steel", "WoodLog"} {
		if food, err := catalog.IsFood(name); err != nil || food {
			t.Errorf("%s is no food: %v %v", name, food, err)
		}
	}
	foods, err := catalog.Foods()
	if err != nil || len(foods) < 50 {
		t.Fatalf("%d foods, %v", len(foods), err)
	}
	ingredients := map[string]policy.MealIngredients{}
	for _, food := range foods {
		meal := food.Kind == policy.FoodKindMealAwful || food.Kind == policy.FoodKindMealSimple || food.Kind == policy.FoodKindMealFine || food.Kind == policy.FoodKindMealLavish
		if meal != (food.Ingredients != "") {
			t.Errorf("%s ingredients %q on kind %q", food.Def, food.Ingredients, food.Kind)
		}
		ingredients[food.Def] = food.Ingredients
	}
	if ingredients["MealSimple"] != policy.MealAnyIngredients || ingredients["MealNutrientPaste"] != policy.MealAnyIngredients {
		t.Errorf("meal ingredients %v", ingredients)
	}
}
