package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func foodButcherRequest(days domain.Fact[float64]) FoodOrderRequest {
	bench := ProductionBench{ID: "bench", Definition: "TableButcher", Butcher: true, Usable: domain.Known(true), Token: domain.Known("token"),
		Recipes: []ProductionRecipe{{Name: "ButcherCorpseFlesh", Role: domain.RoleButcherFlesh, Available: domain.Known(true)}},
		Bills:   []ExistingProductionBill{{Role: domain.RoleButcherFlesh, Recipe: "ButcherCorpseFlesh"}}}
	return FoodOrderRequest{Purpose: ButcherFood, Benches: domain.Known([]ProductionBench{bench}), TargetDays: 7,
		Facts: RoundsFacts{Colonists: domain.Known(int64(3)), FoodDays: days}}
}

func standingButcher() []GearBench {
	spec := OrderSpec{Recipe: "ButcherCorpseFlesh", Mode: domain.ButcherForever, BenchKind: "TableButcher"}
	return []GearBench{{ID: "bench", Def: "TableButcher", Bills: domain.Known([]GearBill{{ID: "Bill_1", Recipe: "ButcherCorpseFlesh", Role: domain.RoleButcherFlesh, Spec: domain.Known(spec)}})}}
}

// An owed purpose declares the bill that should stand whatever stands now.
func TestDeclareFoodOrdersOwedButcher(t *testing.T) {
	want := OrderSpec{Recipe: "ButcherCorpseFlesh", Mode: domain.ButcherForever, BenchKind: "TableButcher"}
	for _, benches := range [][]GearBench{nil, standingButcher()} {
		got := DeclareFoodOrders(foodButcherRequest(domain.Known(2.0)), benches)
		if got.Abstained() || !reflect.DeepEqual(got.Orders, []OrderSpec{want}) {
			t.Fatalf("%+v", got)
		}
	}
}

// A purpose that is not owed keeps the bills that stand for it, and declares
// nothing when none stands; an unread runway abstains.
func TestDeclareFoodOrdersNotOwedKeepsStanding(t *testing.T) {
	got := DeclareFoodOrders(foodButcherRequest(domain.Known(20.0)), standingButcher())
	if got.Abstained() || len(got.Orders) != 1 || got.Orders[0].Recipe != "ButcherCorpseFlesh" {
		t.Fatalf("standing butcher bill dropped: %+v", got)
	}
	if got = DeclareFoodOrders(foodButcherRequest(domain.Known(20.0)), nil); got.Abstained() || len(got.Orders) != 0 {
		t.Fatalf("%+v", got)
	}
	if got = DeclareFoodOrders(foodButcherRequest(domain.Unknown[float64]()), standingButcher()); !got.Abstained() {
		t.Fatalf("an unread runway declared %+v", got)
	}
}

// A food plan that has not opened the capacity, or a standing bill that is
// unread, is an abstain: the ledger removes nothing on it.
func TestDeclareFoodOrdersAbstains(t *testing.T) {
	cook := foodButcherRequest(domain.Known(2.0))
	cook.Purpose = CookFood
	if got := DeclareFoodOrders(cook, nil); !got.Abstained() {
		t.Fatalf("cooking without a food plan declared %+v", got)
	}
	unread := standingButcher()
	unread[0].Bills = domain.Unknown[[]GearBill]()
	if got := DeclareFoodOrders(foodButcherRequest(domain.Known(20.0)), unread); !got.Abstained() {
		t.Fatalf("%+v", got)
	}
}

// The cook-ahead bill ends with the flare: with no outage nothing is declared,
// so a standing cook-ahead bill is an orphan.
func TestDeclareFoodOrdersCookAheadEndsWithTheFlare(t *testing.T) {
	r := foodButcherRequest(domain.Known(2.0))
	r.Purpose = CookAheadFood
	if got := DeclareFoodOrders(r, standingButcher()); got.Abstained() || len(got.Orders) != 0 {
		t.Fatalf("%+v", got)
	}
}

// A stock-target bill that already covers the wanted target stands as it is,
// whatever the target has since drifted to; a short one is replaced.
func TestAdequateOrder(t *testing.T) {
	standing := func(target int32) []GearBench {
		spec := OrderSpec{Recipe: "Cook", Mode: domain.StockTarget, Target: target, BenchKind: "Stove"}
		return []GearBench{{ID: "b", Def: "Stove", Bills: domain.Known([]GearBill{{ID: "Bill_1", Spec: domain.Known(spec)}})}}
	}
	want := OrderSpec{Recipe: "Cook", Mode: domain.FoodTarget, Target: 9, BenchKind: "Stove"}
	if got, _ := adequateOrder(want, standing(30)); got.Target != 30 {
		t.Fatalf("an adequate bill was replaced: %+v", got)
	}
	if got, _ := adequateOrder(want, standing(3)); !reflect.DeepEqual(got, want) {
		t.Fatalf("a short bill was kept: %+v", got)
	}
}
