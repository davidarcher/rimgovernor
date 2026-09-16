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

// preserveKibbleBench builds a single usable bench offering an unclaimed
// PreserveFood recipe (0.5 nutrition/unit, demand 2/day, non-perishable), plus
// an existing MakePemmican bill on a second bench reserving its own target.
func preserveKibbleBench(reservedTarget int32, reservedForever bool) domain.Fact[[]ProductionBench] {
	kibble := ProductionBench{
		ID: "bench-kibble", Usable: domain.Known(true), Token: domain.Known("tok-kibble"),
		Recipes: []ProductionRecipe{{
			Name: "MakeKibble", Available: domain.Known(true),
			Products: []ProductionProduct{{Name: "Kibble", Nutrition: domain.Known(0.5), Edible: domain.Known(true), Perishable: domain.Known(false), Demand: domain.Known(2.0)}},
		}},
	}
	reserve := pemmicanBench("bench-pemmican", reservedTarget, reservedForever, true, true)
	reserve.Usable, reserve.Token = domain.Known(true), domain.Known("tok-pemmican")
	return domain.Known([]ProductionBench{kibble, reserve})
}

func TestSelectProductionBillPreserveFoodWithoutReservedStock(t *testing.T) {
	benches := preserveKibbleBench(0, false)
	selection, ok := SelectProductionBill(PreserveFood, benches, domain.Known(int64(5)), domain.Known(1.0), domain.Known(1.0), 7)
	if !ok {
		t.Fatal("expected a bill with no reserved stock offsetting demand")
	}
	// demand(2)*targetDays(7) = 14 nutrition / 0.5 per unit = 28 units.
	if selection.Target != 28 {
		t.Fatal(selection)
	}
}

func TestSelectProductionBillPreserveFoodNetsPartialReservedStock(t *testing.T) {
	// The pemmican bill already reserves 10 nutrition (20 units * 0.5), so the
	// kibble bill only needs to cover the remaining 4 nutrition.
	benches := preserveKibbleBench(20, false)
	selection, ok := SelectProductionBill(PreserveFood, benches, domain.Known(int64(5)), domain.Known(1.0), domain.Known(1.0), 7)
	if !ok {
		t.Fatal("expected a bill for the unreserved remainder")
	}
	if selection.Target != 8 {
		t.Fatal(selection)
	}
}

func TestSelectProductionBillPreserveFoodSkipsWhenReservedStockCoversDemand(t *testing.T) {
	// 40 units * 0.5 nutrition = 20, fully covering demand(2)*targetDays(7)=14.
	benches := preserveKibbleBench(40, false)
	if _, ok := SelectProductionBill(PreserveFood, benches, domain.Known(int64(5)), domain.Known(1.0), domain.Known(1.0), 7); ok {
		t.Fatal("reserved stock already covering demand should not trigger a new bill")
	}
}

func TestSelectProductionBillPreserveFoodIgnoresForeverReservation(t *testing.T) {
	// A Forever-mode bill never reserves a bounded nutrition amount, so its
	// target must not offset the new bill's own demand-based target.
	benches := preserveKibbleBench(40, true)
	selection, ok := SelectProductionBill(PreserveFood, benches, domain.Known(int64(5)), domain.Known(1.0), domain.Known(1.0), 7)
	if !ok || selection.Target != 28 {
		t.Fatal(selection, ok)
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
