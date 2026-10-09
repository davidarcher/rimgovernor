package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func resourceOrderBench(bills ...GearBill) GearBench {
	return GearBench{ID: "bench1", Def: "TableMachining", Bills: domain.Known(bills), Recipes: domain.Known([]GearRecipe{{
		Definition: "Make_Steel", Products: []Resource{"Steel"}, Available: domain.Known(true), AvailableOn: domain.Known(true),
	}, {
		Definition: "Make_Wort", Products: []Resource{"Wort"}, Available: domain.Known(true), AvailableOn: domain.Known(true),
	}})}
}

func resourceOrderRequest(stock int64, bench GearBench, floors ...ResourceFloor) ResourceOrderRequest {
	return ResourceOrderRequest{Floors: floors, Stock: domain.Known(map[Resource]int64{"Steel": stock}), Benches: domain.Known([]GearBench{bench})}
}

// A floor below its target declares the producing recipe's stock target, sized
// by the dispatcher (the order carries the product and class); the beer reserve
// is wanted whatever the stock.
func TestDeclareResourceOrdersDeclaresDeficitsAndReserve(t *testing.T) {
	got, err := DeclareResourceOrders(resourceOrderRequest(0, resourceOrderBench(), ResourceFloor{Resource: "Steel", Target: 50}, ResourceFloor{Resource: "Wort", Target: 8, Reserve: true}))
	want := []OrderSpec{
		{Recipe: "Make_Steel", Mode: domain.StockTarget, Target: 50, BenchKind: "TableMachining", Product: "Steel", Class: ResourceMaterial},
		{Recipe: "Make_Wort", Mode: domain.BeerReserve, Target: 8, BenchKind: "TableMachining"},
	}
	if err != nil || got.Abstain || !reflect.DeepEqual(got.Orders, want) {
		t.Fatal(got, err)
	}
	if got, err = DeclareResourceOrders(resourceOrderRequest(50, resourceOrderBench(), ResourceFloor{Resource: "Steel", Target: 50})); err != nil || got.Abstain || len(got.Orders) != 0 {
		t.Fatal("a met floor declared", got, err)
	}
}

// A bill that already makes the resource is declared as it stands, met floor or
// not, so it is kept; an idle bill of another size is replaced, not kept beside.
func TestDeclareResourceOrdersKeepsStandingBills(t *testing.T) {
	standing := OrderSpec{Recipe: "Make_Steel", Mode: domain.StockTarget, Target: 50, BenchKind: "TableMachining"}
	bill := GearBill{ID: "b", Recipe: "Make_Steel", Products: []Resource{"Steel"}, Active: domain.Known(true), Spec: domain.Known(standing)}
	for _, stock := range []int64{0, 50} {
		got, err := DeclareResourceOrders(resourceOrderRequest(stock, resourceOrderBench(bill), ResourceFloor{Resource: "Steel", Target: 50}))
		if err != nil || got.Abstain || !reflect.DeepEqual(got.Orders, []OrderSpec{standing}) {
			t.Fatal(stock, got, err)
		}
	}
	idle := bill
	idle.Active = domain.Known(false)
	idle.Spec = domain.Known(OrderSpec{Recipe: "Make_Steel", Mode: domain.StockTarget, Target: 20, BenchKind: "TableMachining"})
	got, err := DeclareResourceOrders(resourceOrderRequest(0, resourceOrderBench(idle), ResourceFloor{Resource: "Steel", Target: 50}))
	if err != nil || len(got.Orders) != 1 || got.Orders[0].Target != 50 {
		t.Fatal(got, err)
	}
}

// Unread stock, benches or bill identity abstain: the ledger removes nothing.
func TestDeclareResourceOrdersAbstainsOnUnknowns(t *testing.T) {
	floor := ResourceFloor{Resource: "Steel", Target: 50}
	r := resourceOrderRequest(0, resourceOrderBench(), floor)
	r.Stock = domain.Unknown[map[Resource]int64]()
	if got, _ := DeclareResourceOrders(r); !got.Abstain {
		t.Fatal("unread stock declared")
	}
	r = resourceOrderRequest(0, resourceOrderBench(), floor)
	r.Benches = domain.Unknown[[]GearBench]()
	if got, _ := DeclareResourceOrders(r); !got.Abstain {
		t.Fatal("unread benches declared")
	}
	unread := GearBill{ID: "b", Products: []Resource{"Steel"}, Active: domain.Known(true)}
	if got, _ := DeclareResourceOrders(resourceOrderRequest(0, resourceOrderBench(unread), floor)); !got.Abstain {
		t.Fatal("unread bill declared")
	}
}
