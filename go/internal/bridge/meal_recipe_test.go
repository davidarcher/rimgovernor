package bridge

import (
	"math"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestColonyProductionAdmitsMealFacts(t *testing.T) {
	for _, change := range []string{"valid", "absent", "nan", "negative-work", "zero-efficiency", "empty-slot", "bad-class", "duplicate-class", "mixed-any", "invalid-skill"} {
		t.Run(change, func(t *testing.T) {
			v := productionFixture(t)
			r := v.Cooking[0].Recipes[0]
			r.Mood, r.NutrientEfficiency, r.WorkPerNutrition, r.NeedsPower = proto.Float64(5), proto.Float64(1.8), proto.Float64(500), proto.Bool(false)
			r.Skills = []*o.SkillRequirement{{DefName: proto.String("Cooking"), Minimum: proto.Int32(6)}}
			r.IngredientClasses = &o.RecipeIngredientClasses{Slots: []*o.FoodIngredientSlot{{Alternatives: []o.FoodIngredientClass{o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_MEAT, o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANIMAL_PRODUCT}}, {Alternatives: []o.FoodIngredientClass{o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_VEGETABLE}}}}
			switch change {
			case "absent":
				r.Mood, r.NutrientEfficiency, r.WorkPerNutrition, r.NeedsPower, r.Skills, r.IngredientClasses = nil, nil, nil, nil, nil, nil
			case "nan":
				r.Mood = proto.Float64(math.NaN())
			case "negative-work":
				r.WorkPerNutrition = proto.Float64(-1)
			case "zero-efficiency":
				r.NutrientEfficiency = proto.Float64(0)
			case "empty-slot":
				r.IngredientClasses.Slots[0].Alternatives = nil
			case "bad-class":
				r.IngredientClasses.Slots[0].Alternatives[0] = 99
			case "duplicate-class":
				r.IngredientClasses.Slots[0].Alternatives[0] = r.IngredientClasses.Slots[0].Alternatives[1]
			case "mixed-any":
				r.IngredientClasses.Slots[0].Alternatives[0] = o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANY
			case "invalid-skill":
				r.Skills[0].Minimum = nil
			}
			err := ValidateColonyFacts(v, v.Context.Identity)
			if (err == nil) != (change == "valid" || change == "absent") {
				t.Fatal(err)
			}
		})
	}
}
