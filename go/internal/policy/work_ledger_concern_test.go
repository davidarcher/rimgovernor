package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func ledgerTestBench(id string, bills int, recipes ...string) GearBench {
	rows := make([]GearRecipe, 0, len(recipes))
	for _, r := range recipes {
		rows = append(rows, GearRecipe{Definition: r, AvailableOn: domain.Known(true)})
	}
	b := make([]GearBill, bills)
	for i := range b {
		b[i] = GearBill{ID: id + "-bill", Spec: domain.Known(ledgerSpec("x"))}
	}
	return GearBench{ID: id, Def: "TableMachining", Usable: domain.Known(true), Bills: domain.Known(b), Recipes: domain.Known(rows)}
}

func TestPlaceLedgerOrders(t *testing.T) {
	vest, hat := ledgerSpec("vest"), ledgerSpec("hat")
	full := ledgerTestBench("a", LedgerBenchSlots, "vest")
	light := ledgerTestBench("b", 1, "vest", "hat")
	other := ledgerTestBench("c", 0, "vest")
	other.Def = "Other"
	unusable := ledgerTestBench("d", 0, "vest")
	unusable.Usable = domain.Known(false)
	placed, unplaced := PlaceLedgerOrders([]OrderSpec{vest, hat}, []GearBench{full, light, other, unusable})
	if len(unplaced) != 0 || len(placed) != 2 || placed[0].Bench != "b" || placed[1].Bench != "b" {
		t.Fatalf("placed = %+v unplaced = %+v", placed, unplaced)
	}
	// One bill per bench and recipe in a plan: a second vest order waits.
	second := vest
	second.Target = 5
	placed, unplaced = PlaceLedgerOrders([]OrderSpec{vest, second}, []GearBench{light})
	if len(placed) != 1 || len(unplaced) != 1 {
		t.Fatalf("placed = %+v unplaced = %+v", placed, unplaced)
	}
	if _, unplaced = PlaceLedgerOrders([]OrderSpec{ledgerSpec("cape")}, []GearBench{light}); len(unplaced) != 1 {
		t.Fatal("an order no bench offers was placed")
	}
}

func TestLedgerActualsUnknownReadback(t *testing.T) {
	b := ledgerTestBench("a", 1, "vest")
	if actual, ok := LedgerActuals([]GearBench{b}); !ok || len(actual) != 1 || actual[0].Bench != "a" {
		t.Fatalf("actual = %+v %v", actual, ok)
	}
	unread := b
	unread.Bills = domain.Unknown[[]GearBill]()
	noSpec := ledgerTestBench("a", 1, "vest")
	bills, _ := noSpec.Bills.Value()
	bills[0].Spec = domain.Unknown[OrderSpec]()
	for name, benches := range map[string][]GearBench{"bills": {b, unread}, "spec": {noSpec}} {
		if _, ok := LedgerActuals(benches); ok {
			t.Errorf("%s: a partial readback was diffed", name)
		}
	}
}

func TestOrderSpecKeyIsWireClass(t *testing.T) {
	food := OrderSpec{Recipe: "r", Mode: domain.FoodTarget, Target: 3}
	stock := OrderSpec{Recipe: "r", Mode: domain.StockTarget, Target: 3}
	if food.Key() != stock.Key() {
		t.Fatal("native cannot tell a food target from a stock target")
	}
}

func TestReconcileLedgerSpentBillNeverSatisfies(t *testing.T) {
	spec := ledgerSpec("vest")
	plan := ReconcileLedger([]Declared{{Orders: []OrderSpec{spec}}}, []ActualBill{{ID: "1", Bench: "b", Spec: spec, Spent: true, Migrated: true}}, nil, nil)
	if len(plan.Place) != 1 || len(plan.Keep) != 1 || plan.Orphans["1"] != 1 {
		t.Fatalf("plan = %+v", plan)
	}
}
