package bridge

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The food rules the game's code states over the def rows (native used to
// classify every food and send the answer): which defs a food policy can
// allow, the kind each is for diets and a meal's ingredients.

const corpseClass = "Verse.Corpse"

// foodFlag reports whether the ingestible's food type has any bit of flag.
func foodFlag(row *d.ThingDef, flag d.FoodTypeFlags) bool {
	return int32(row.GetIngestible().GetFoodType())&int32(flag) != 0
}

// foodFlagAll is Enum.HasFlag: every bit of flag.
func foodFlagAll(row *d.ThingDef, flag d.FoodTypeFlags) bool {
	return int32(row.GetIngestible().GetFoodType())&int32(flag) == int32(flag)
}

// isFood is the defs a policy can allow: a nutrition-giving ingestible that is
// no drug and no corpse (ThingDef.IsNutritionGivingIngestible, IsDrug,
// IsCorpse; the nutrition is IngestibleProperties.CachedNutrition, the
// Nutrition stat of the def).
func (catalog *DefinitionCatalog) isFood(name string, row *d.ThingDef) (bool, error) {
	ing := row.GetIngestible()
	if ing == nil || ing.GetDrugCategory() != d.DrugCategory_DRUG_CATEGORY_NONE {
		return false, nil
	}
	if corpse, err := catalog.ClassIsA(row.GetThingClass(), corpseClass); err != nil || corpse {
		return false, err
	}
	nutrition, shown, err := catalog.ShownStatValue(name, "", StatNutrition)
	return err == nil && shown && nutrition > 0, err
}

// FoodKindOf is the kind the def has for diets: meals by their preferability
// tier, then kibble, hay, meat by its source (human, insect, other), fungus,
// animal product, a vegetable or fruit food type, else other. Empty, with
// ok false, for a def that is no food.
func (catalog *DefinitionCatalog) FoodKindOf(name string) (policy.FoodKind, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return "", err
	}
	food, err := catalog.isFood(name, row)
	if err != nil || !food {
		return "", err
	}
	return catalog.foodKind(name, row)
}

func (catalog *DefinitionCatalog) foodKind(name string, row *d.ThingDef) (policy.FoodKind, error) {
	switch row.GetIngestible().GetPreferability() {
	case d.FoodPreferability_FOOD_PREFERABILITY_MEAL_AWFUL:
		return policy.FoodKindMealAwful, nil
	case d.FoodPreferability_FOOD_PREFERABILITY_MEAL_SIMPLE:
		return policy.FoodKindMealSimple, nil
	case d.FoodPreferability_FOOD_PREFERABILITY_MEAL_FINE:
		return policy.FoodKindMealFine, nil
	case d.FoodPreferability_FOOD_PREFERABILITY_MEAL_LAVISH:
		return policy.FoodKindMealLavish, nil
	}
	switch {
	case foodFlag(row, d.FoodTypeFlags_FOOD_TYPE_FLAGS_KIBBLE):
		return policy.FoodKindKibble, nil
	case name == string(policy.HayResource):
		return policy.FoodKindHay, nil
	}
	meat, err := catalog.RawMeat(name)
	if err != nil {
		return "", err
	}
	switch {
	case meat:
		return catalog.meatSourceKind(row), nil
	case foodFlagAll(row, d.FoodTypeFlags_FOOD_TYPE_FLAGS_FUNGUS):
		return policy.FoodKindFungus, nil
	case foodFlagAll(row, d.FoodTypeFlags_FOOD_TYPE_FLAGS_ANIMAL_PRODUCT):
		return policy.FoodKindAnimalProduct, nil
	case foodFlag(row, d.FoodTypeFlags_FOOD_TYPE_FLAGS_VEGETABLE_OR_FRUIT):
		return policy.FoodKindVegetable, nil
	}
	return policy.FoodKindOther, nil
}

// meatSourceKind is FoodUtility.GetMeatSourceCategory of a raw meat: human
// when its source race is humanlike, insect when the source's flesh is
// insectoid, else ordinary raw meat.
func (catalog *DefinitionCatalog) meatSourceKind(row *d.ThingDef) policy.FoodKind {
	if !foodFlagAll(row, d.FoodTypeFlags_FOOD_TYPE_FLAGS_MEAT) {
		return policy.FoodKindRawMeat
	}
	source := row.GetIngestible().GetSourceDef()
	if race := catalog.ThingDef(source).GetRace(); race != nil && race.GetIntelligence() >= d.Intelligence_INTELLIGENCE_HUMANLIKE {
		return policy.FoodKindHumanMeat
	}
	if _, insect := catalog.RaceFlags(source); insect {
		return policy.FoodKindInsectMeat
	}
	return policy.FoodKindRawMeat
}

// mealIngredientsOf is FoodUtility.GetFoodKind of a meal def: meat for a raw
// meat or a meat or corpse food type, any for an animal product, else the
// ingredients comp's noIngredientsFoodKind (non-meat without the comp).
func (catalog *DefinitionCatalog) mealIngredientsOf(name string, row *d.ThingDef) (policy.MealIngredients, error) {
	meat, err := catalog.RawMeat(name)
	if err != nil {
		return "", err
	}
	switch {
	case meat, foodFlag(row, d.FoodTypeFlags_FOOD_TYPE_FLAGS_MEAT), foodFlag(row, d.FoodTypeFlags_FOOD_TYPE_FLAGS_CORPSE):
		return policy.MealMeatOnly, nil
	case foodFlagAll(row, d.FoodTypeFlags_FOOD_TYPE_FLAGS_ANIMAL_PRODUCT):
		return policy.MealAnyIngredients, nil
	}
	if comp := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Ingredients); comp != nil {
		switch comp.GetNoIngredientsFoodKind() {
		case d.FoodKind_FOOD_KIND_MEAT:
			return policy.MealMeatOnly, nil
		case d.FoodKind_FOOD_KIND_ANY:
			return policy.MealAnyIngredients, nil
		}
	}
	return policy.MealNonMeat, nil
}

// Foods is every food a policy can allow with its kind and, for a meal, its
// ingredients, sorted by name.
func (catalog *DefinitionCatalog) Foods() ([]policy.Food, error) {
	if catalog == nil {
		return nil, nil
	}
	catalog.foodsOnce.Do(func() { catalog.foods, catalog.foodsErr = catalog.buildFoods() })
	return catalog.foods, catalog.foodsErr
}

func (catalog *DefinitionCatalog) buildFoods() ([]policy.Food, error) {
	var foods []policy.Food
	for name, row := range catalog.ThingDefs {
		food, err := catalog.isFood(name, row)
		if err != nil {
			return nil, err
		}
		if !food {
			continue
		}
		kind, err := catalog.foodKind(name, row)
		if err != nil {
			return nil, err
		}
		entry := policy.Food{Def: name, Kind: kind}
		if kind == policy.FoodKindMealAwful || kind == policy.FoodKindMealSimple || kind == policy.FoodKindMealFine || kind == policy.FoodKindMealLavish {
			if entry.Ingredients, err = catalog.mealIngredientsOf(name, row); err != nil {
				return nil, err
			}
		}
		foods = append(foods, entry)
	}
	slices.SortFunc(foods, func(a, b policy.Food) int { return strings.Compare(a.Def, b.Def) })
	return foods, nil
}
