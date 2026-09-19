package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func pemmicanBench(id string, target int32, forever bool, targetKnown, foreverKnown bool) ProductionBench {
	bill := ExistingProductionBill{Recipe: "MakePemmican"}
	bill.TargetCount = domain.Unknown[int32]()
	if targetKnown {
		bill.TargetCount = domain.Known(target)
	}
	bill.Forever = domain.Unknown[bool]()
	if foreverKnown {
		bill.Forever = domain.Known(forever)
	}
	return ProductionBench{
		ID: id,
		Recipes: []ProductionRecipe{{
			Name:     "MakePemmican",
			Products: []ProductionProduct{{Name: "Pemmican", Nutrition: domain.Known(0.5), Edible: domain.Known(true)}},
		}},
		Bills: []ExistingProductionBill{bill},
	}
}

func TestReservedFoodNutritionSumsTargetBills(t *testing.T) {
	total, ok := ReservedFoodNutrition([]ProductionBench{pemmicanBench("bench-1", 20, false, true, true)})
	if !ok || total != 10 {
		t.Fatal(total, ok)
	}
}

func TestReservedFoodNutritionIgnoresForeverBills(t *testing.T) {
	total, ok := ReservedFoodNutrition([]ProductionBench{pemmicanBench("bench-1", 20, true, true, true)})
	if !ok || total != 0 {
		t.Fatal(total, ok)
	}
}

func TestReservedFoodNutritionIgnoresButcherBenches(t *testing.T) {
	bench := pemmicanBench("bench-1", 20, false, true, true)
	bench.Butcher = true
	total, ok := ReservedFoodNutrition([]ProductionBench{bench})
	if !ok || total != 0 {
		t.Fatal(total, ok)
	}
}

func TestReservedFoodNutritionSkipsUnknownTargetOrMode(t *testing.T) {
	unknownTarget, ok := ReservedFoodNutrition([]ProductionBench{pemmicanBench("bench-1", 20, false, false, true)})
	if !ok || unknownTarget != 0 {
		t.Fatal(unknownTarget, ok)
	}
	unknownForever, ok := ReservedFoodNutrition([]ProductionBench{pemmicanBench("bench-1", 20, false, true, false)})
	if !ok || unknownForever != 0 {
		t.Fatal(unknownForever, ok)
	}
}

func TestReservedFoodNutritionSkipsUnmatchedRecipe(t *testing.T) {
	bench := pemmicanBench("bench-1", 20, false, true, true)
	bench.Bills[0].Recipe = "CookMealSimple"
	total, ok := ReservedFoodNutrition([]ProductionBench{bench})
	if !ok || total != 0 {
		t.Fatal(total, ok)
	}
}

func TestReservedFoodNutritionSumsAcrossBenches(t *testing.T) {
	total, ok := ReservedFoodNutrition([]ProductionBench{
		pemmicanBench("bench-1", 20, false, true, true),
		pemmicanBench("bench-2", 8, false, true, true),
	})
	if !ok || total != 14 {
		t.Fatal(total, ok)
	}
}

func TestReservedFoodNutritionRejectsOversizedInput(t *testing.T) {
	benches := make([]ProductionBench, 257)
	if _, ok := ReservedFoodNutrition(benches); ok {
		t.Fatal("oversized bench list accepted")
	}
}

func TestSelectProductionBillPrefersSeparatedButcherBench(t *testing.T) {
	butcher := func(id, room string) ProductionBench {
		bench := ProductionBench{ID: id, Definition: "ButcherSpot", Token: domain.Known("t-" + id), Usable: domain.Known(true), Butcher: true, Recipes: []ProductionRecipe{{Name: "ButcherCorpseFlesh", Available: domain.Known(true)}}}
		if room != "" {
			bench.Room = domain.Known(room)
		}
		return bench
	}
	stove := ProductionBench{ID: "stove", Definition: "FueledStove", Token: domain.Known("t-stove"), Usable: domain.Known(true), Room: domain.Known("kitchen")}
	// Alphabetically "a" would win; the separated bench "b" wins instead, and
	// an unknown room is never certified separated.
	benches := domain.Known([]ProductionBench{butcher("a", "kitchen"), butcher("b", "yard"), butcher("c", ""), stove})
	selection, ok := SelectProductionBill(ButcherFood, benches, domain.Known(int64(3)), domain.Unknown[float64](), domain.Unknown[float64](), 7)
	if !ok || selection.Bench != "b" || selection.Mode != domain.ButcherForever {
		t.Fatal(selection, ok)
	}
	benches = domain.Known([]ProductionBench{butcher("a", "kitchen"), butcher("c", ""), stove})
	if selection, ok := SelectProductionBill(ButcherFood, benches, domain.Known(int64(3)), domain.Unknown[float64](), domain.Unknown[float64](), 7); !ok || selection.Bench != "a" {
		t.Fatal(selection, ok)
	}
}

func TestAllButchersColocated(t *testing.T) {
	butcher := func(id, room string) ProductionBench {
		bench := ProductionBench{ID: id, Definition: "ButcherSpot", Butcher: true}
		if room != "" {
			bench.Room = domain.Known(room)
		}
		return bench
	}
	stove := ProductionBench{ID: "stove", Definition: "FueledStove", Room: domain.Known("kitchen")}
	cases := []struct {
		name    string
		benches []ProductionBench
		want    bool
	}{
		{"shared only", []ProductionBench{butcher("a", "kitchen"), stove}, true},
		{"one apart", []ProductionBench{butcher("a", "kitchen"), butcher("b", "yard"), stove}, false},
		{"unknown room", []ProductionBench{butcher("a", "kitchen"), butcher("c", ""), stove}, false},
		{"no butcher", []ProductionBench{stove}, false},
		{"no cooking", []ProductionBench{butcher("a", "yard")}, false},
	}
	for _, tc := range cases {
		if got := AllButchersColocated(tc.benches); got != tc.want {
			t.Errorf("%s: got %v want %v", tc.name, got, tc.want)
		}
	}
}

// cookAheadBenches is a fuelled stove already cooking simple meals to a
// small target beside an unusable (unpowered under the flare) electric
// stove; both offer CookMealSimple at 0.9 nutrition a meal.
func cookAheadBenches(existingTarget int32) domain.Fact[[]ProductionBench] {
	meal := ProductionRecipe{Name: "CookMealSimple", Available: domain.Known(true),
		Products: []ProductionProduct{{Name: "MealSimple", Nutrition: domain.Known(0.9), Edible: domain.Known(true), Perishable: domain.Known(true), RotDays: domain.Known(4.0), Demand: domain.Known(2.0)}}}
	fuelled := ProductionBench{ID: "bench-fuelled", Usable: domain.Known(true), Token: domain.Known("tok-fuelled"), Recipes: []ProductionRecipe{meal}}
	if existingTarget > 0 {
		fuelled.Bills = []ExistingProductionBill{{Recipe: "CookMealSimple", TargetCount: domain.Known(existingTarget), Forever: domain.Known(false)}}
	}
	electric := ProductionBench{ID: "bench-electric", Usable: domain.Known(false), Token: domain.Known("tok-electric"), Recipes: []ProductionRecipe{meal}}
	return domain.Known([]ProductionBench{electric, fuelled})
}

func TestSelectProductionBillCookAheadCooksAtRiskStockBeyondReservedBills(t *testing.T) {
	// 18 nutrition at risk, nothing reserved: 20 meals on the usable bench.
	selection, ok := SelectProductionBill(CookAheadFood, cookAheadBenches(0), domain.Known(int64(3)), domain.Unknown[float64](), domain.Known(18.0), 7)
	if !ok || selection.Bench != "bench-fuelled" || selection.Recipe != "CookMealSimple" || selection.Mode != domain.FoodTarget || selection.Target != 20 {
		t.Fatal(selection, ok)
	}
	// An existing 9-meal bill reserves 8.1: the second bill on the same
	// bench and recipe covers the remaining 9.9 (11 meals).
	selection, ok = SelectProductionBill(CookAheadFood, cookAheadBenches(9), domain.Known(int64(3)), domain.Unknown[float64](), domain.Known(18.0), 7)
	if !ok || selection.Bench != "bench-fuelled" || selection.Target != 11 {
		t.Fatal(selection, ok)
	}
	// Reserved beyond the risk, or no known risk: no bill.
	if _, ok = SelectProductionBill(CookAheadFood, cookAheadBenches(30), domain.Known(int64(3)), domain.Unknown[float64](), domain.Known(18.0), 7); ok {
		t.Fatal("reserved bills already cover the at-risk stock")
	}
	if _, ok = SelectProductionBill(CookAheadFood, cookAheadBenches(0), domain.Known(int64(3)), domain.Unknown[float64](), domain.Unknown[float64](), 7); ok {
		t.Fatal("unknown at-risk nutrition raised a bill")
	}
	// The ordinary cook purpose still refuses to double an existing bill.
	if _, ok = SelectProductionBill(CookFood, cookAheadBenches(9), domain.Known(int64(3)), domain.Unknown[float64](), domain.Unknown[float64](), 7); ok {
		t.Fatal("cook duplicated the bench's existing bill")
	}
}
