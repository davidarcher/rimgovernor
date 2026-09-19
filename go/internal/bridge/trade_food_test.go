package bridge

import (
	"math"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestTradeFoodClassification(t *testing.T) {
	for _, tc := range []struct {
		name   string
		change func(*o.TradeLine)
		valid  bool
	}{
		{"crop", func(*o.TradeLine) {}, true},
		{"absent", func(v *o.TradeLine) { v.Food = nil }, true},
		{"nan", func(v *o.TradeLine) { v.Food.Nutrition = math.NaN() }, false},
		{"zero", func(v *o.TradeLine) { v.Food.Nutrition = 0 }, false},
		{"unknown class", func(v *o.TradeLine) { v.Food.IngredientClass = o.FoodIngredientClass(99) }, false},
		{"prepared crop", func(v *o.TradeLine) { v.Food.Prepared = true }, false},
		{"meat crop", func(v *o.TradeLine) { v.Food.IngredientClass = o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_MEAT }, false},
		{"pawn", func(v *o.TradeLine) { v.Pawn = proto.Bool(true) }, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			line := tradeSheetLine("crop", "Rice", 100, 0)
			line.Food = &o.TradeFoodFacts{Nutrition: 0.05, IngredientClass: o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_VEGETABLE, Crop: true}
			tc.change(line)
			row, err := tradeSheetRow(line)
			if (err == nil) != tc.valid {
				t.Fatalf("row=%+v err=%v", row, err)
			}
			if err == nil && line.Food != nil {
				line.Food.Nutrition = 9
				if row.Food.Nutrition != 0.05 {
					t.Fatal("food classification aliases input")
				}
			}
		})
	}
}
