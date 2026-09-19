package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func fineRecipeFacts() *o.RecipeState {
	return &o.RecipeState{Mood: proto.Float64(5), NutrientEfficiency: proto.Float64(1.8), WorkPerNutrition: proto.Float64(600), NeedsPower: proto.Bool(false), Skills: []*o.SkillRequirement{{DefName: proto.String("Cooking"), Minimum: proto.Int32(6)}}, IngredientClasses: &o.RecipeIngredientClasses{Slots: []*o.FoodIngredientSlot{
		{Alternatives: []o.FoodIngredientClass{o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_MEAT, o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANIMAL_PRODUCT}},
		{Alternatives: []o.FoodIngredientClass{o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_VEGETABLE}},
	}}}
}

func TestMealRecipeFactsPreserveSlotsAndPresence(t *testing.T) {
	var recipe policy.ProductionRecipe
	mealRecipeFacts(fineRecipeFacts(), &recipe)
	slots, known := recipe.IngredientClasses.Value()
	if !known || len(slots) != 2 || len(slots[0].Alternatives) != 2 || slots[0].Alternatives[1] != policy.IngredientAnimalProduct || slots[1].Alternatives[0] != policy.IngredientVegetable {
		t.Fatal(slots, known)
	}
	if value, known := recipe.CookSkillFloor.Value(); !known || value != 6 {
		t.Fatal(recipe)
	}
	if value, known := recipe.NeedsPower.Value(); !known || value {
		t.Fatal(recipe)
	}
	if value, known := recipe.Mood.Value(); !known || value != 5 {
		t.Fatal(recipe)
	}
	if value, known := recipe.NutrientEfficiency.Value(); !known || value != 1.8 {
		t.Fatal(recipe)
	}
	if value, known := recipe.WorkPerNutrition.Value(); !known || value != 600 {
		t.Fatal(recipe)
	}
	var absent policy.ProductionRecipe
	mealRecipeFacts(&o.RecipeState{}, &absent)
	if _, known := absent.IngredientClasses.Value(); known {
		t.Fatal(absent)
	}
	if _, known := absent.CookSkillFloor.Value(); known {
		t.Fatal(absent)
	}
	if _, known := absent.NeedsPower.Value(); known {
		t.Fatal(absent)
	}
}

func TestMealRecipeFactsRejectInvalidClasses(t *testing.T) {
	for _, change := range []func(*o.RecipeState){
		func(r *o.RecipeState) { r.IngredientClasses.Slots = nil },
		func(r *o.RecipeState) { r.IngredientClasses.Slots[0] = nil },
		func(r *o.RecipeState) { r.IngredientClasses.Slots[0].Alternatives = nil },
		func(r *o.RecipeState) { r.IngredientClasses.Slots[0].Alternatives[0] = 999 },
		func(r *o.RecipeState) {
			r.IngredientClasses.Slots[0].Alternatives[0] = r.IngredientClasses.Slots[0].Alternatives[1]
		},
		func(r *o.RecipeState) {
			r.IngredientClasses.Slots[0].Alternatives[0] = o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANY
		},
	} {
		r := fineRecipeFacts()
		change(r)
		var recipe policy.ProductionRecipe
		mealRecipeFacts(r, &recipe)
		if _, known := recipe.IngredientClasses.Value(); known {
			t.Fatal(recipe)
		}
	}
}

func TestMealRecipeNumericFacts(t *testing.T) {
	for _, n := range []float64{math.NaN(), math.Inf(1), -1} {
		var recipe policy.ProductionRecipe
		mealRecipeFacts(&o.RecipeState{NutrientEfficiency: &n, WorkPerNutrition: &n}, &recipe)
		if _, known := recipe.NutrientEfficiency.Value(); known {
			t.Fatal(recipe)
		}
		if _, known := recipe.WorkPerNutrition.Value(); known {
			t.Fatal(recipe)
		}
	}
	var recipe policy.ProductionRecipe
	mealRecipeFacts(&o.RecipeState{Mood: proto.Float64(-4), WorkPerNutrition: proto.Float64(0)}, &recipe)
	if n, known := recipe.Mood.Value(); !known || n != -4 {
		t.Fatal(recipe)
	}
	if n, known := recipe.WorkPerNutrition.Value(); !known || n != 0 {
		t.Fatal(recipe)
	}
}
