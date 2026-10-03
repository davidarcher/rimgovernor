package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
		{"pawn", func(v *o.TradeLine) { v.Pawn = proto.Bool(true) }, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			line := tradeSheetLine("crop", "Rice", 100, 0)
			line.Food = &o.TradeFoodFacts{Nutrition: 0.05}
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

// The trade class of a food def comes from its rows: raw ingredients by thing
// category, meals as the prepared any class, human meat and non-food refused.
func TestTradeFoodReadsTheCatalogRows(t *testing.T) {
	catalog := fullCatalog(t)
	for name, want := range map[string]policy.TradeFoodGood{
		"RawRice":      {Class: policy.IngredientVegetable, Crop: true},
		"Meat_Muffalo": {Class: policy.IngredientMeat},
		"Milk":         {Class: policy.IngredientAnimalProduct},
		"MealSimple":   {Class: policy.IngredientAny, Prepared: true},
		"Pemmican":     {Class: policy.IngredientAny, Prepared: true},
		"InsectJelly":  {Class: policy.IngredientAnimalProduct},
	} {
		got, ok, err := catalog.TradeFood(name)
		if err != nil {
			t.Fatal(name, err)
		}
		if !ok {
			t.Errorf("%s: not a trade food", name)
			continue
		}
		if got.Class != want.Class || got.Prepared != want.Prepared || got.Crop != want.Crop {
			t.Errorf("%s: %+v, want %+v", name, got, want)
		}
	}
	for _, name := range []string{"Meat_Human", "Steel", "Corpse_Human"} {
		if _, ok, err := catalog.TradeFood(name); err != nil || ok {
			t.Errorf("%s: ok %v err %v", name, ok, err)
		}
	}
	if _, _, err := catalog.TradeFood("NoSuchFood"); err == nil {
		t.Error("a def the catalog lacks resolved")
	}
}
