package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func ledgerSpec(recipe string) OrderSpec {
	return OrderSpec{Recipe: recipe, Mode: domain.GearBatch, Target: 2, BenchKind: "TableMachining"}
}

func TestOrderSpecKey(t *testing.T) {
	a := OrderSpec{Recipe: "r", Ingredients: []string{"Steel", "Wood"}}
	b := OrderSpec{Recipe: "r", Ingredients: []string{"Wood", "Steel"}}
	if a.Key() != b.Key() {
		t.Fatal("ingredient order is identity")
	}
	for name, c := range map[string]OrderSpec{
		"worker": {Recipe: "r", Ingredients: a.Ingredients, Worker: "p"},
		"target": {Recipe: "r", Ingredients: a.Ingredients, Target: 1},
		"mode":   {Recipe: "r", Ingredients: a.Ingredients, Mode: domain.StockTarget},
		"bench":  {Recipe: "r", Ingredients: a.Ingredients, BenchKind: "b"},
		"filter": {Recipe: "r", Ingredients: []string{"Steel"}},
	} {
		if c.Key() == a.Key() {
			t.Errorf("%s does not change identity", name)
		}
	}
}

type reconcileCase struct {
	name     string
	declared []Declared
	actual   []ActualBill
	in       map[string]int
	copies   map[string]int
	place    []OrderSpec
	remove   []string
	keep     []string
	out      map[string]int
}

func TestReconcileLedger(t *testing.T) {
	vest, hat := ledgerSpec("vest"), ledgerSpec("hat")
	bill := func(id string, s OrderSpec, k LedgerBillKind) ActualBill {
		return ActualBill{ID: id, Bench: "b1", Kind: k, Spec: s}
	}
	want := func(o ...OrderSpec) []Declared { return []Declared{{Orders: o}} }
	last := OrphanGraceRounds - 1
	tests := []reconcileCase{
		{name: "identical specs coalesce", declared: []Declared{{Orders: []OrderSpec{vest, vest}}, {Orders: []OrderSpec{vest}}}, place: []OrderSpec{vest}, out: map[string]int{}},
		{name: "missing bill is placed", declared: want(vest, hat), actual: []ActualBill{bill("1", vest, LedgerProduction)}, place: []OrderSpec{hat}, keep: []string{"1"}, out: map[string]int{}},
		{name: "matching bill kept", declared: want(vest), actual: []ActualBill{bill("1", vest, LedgerProduction)}, keep: []string{"1"}, out: map[string]int{}},
		{name: "orphan counts up", declared: want(), actual: []ActualBill{bill("1", vest, LedgerProduction)}, keep: []string{"1"}, out: map[string]int{"1": 1}},
		{name: "abstain skips removal and freezes counter", declared: []Declared{{Abstains: []Abstain{{Fact: CauseUnreadStock}}}}, actual: []ActualBill{bill("1", vest, LedgerProduction)}, in: map[string]int{"1": last}, keep: []string{"1"}, out: map[string]int{"1": last}},
		{name: "one abstainer among declarers still skips removal", declared: []Declared{{Orders: []OrderSpec{hat}}, {Abstains: []Abstain{{Fact: CauseUnreadStock}}}}, actual: []ActualBill{bill("1", vest, LedgerProduction)}, in: map[string]int{"1": last}, place: []OrderSpec{hat}, keep: []string{"1"}, out: map[string]int{"1": last}},
		{name: "orphan removed after grace", declared: want(), actual: []ActualBill{bill("1", vest, LedgerProduction)}, in: map[string]int{"1": last}, remove: []string{"1"}, out: map[string]int{}},
		{name: "restart only delays removal", declared: want(), actual: []ActualBill{bill("1", vest, LedgerProduction)}, keep: []string{"1"}, out: map[string]int{"1": 1}},
		{name: "duplicate of a wanted spec is an orphan", declared: want(vest), actual: []ActualBill{bill("1", vest, LedgerProduction), bill("2", vest, LedgerProduction)}, in: map[string]int{"2": last}, keep: []string{"1"}, remove: []string{"2"}, out: map[string]int{}},
		{name: "redeclared orphan resets", declared: want(vest), actual: []ActualBill{bill("1", vest, LedgerProduction)}, in: map[string]int{"1": 2}, keep: []string{"1"}, out: map[string]int{}},
	}
	for _, k := range []LedgerBillKind{LedgerMechGestation, LedgerSurgery, LedgerMedical, LedgerBabyFood} {
		tests = append(tests,
			reconcileCase{name: "excluded " + string(k) + " never removed", declared: want(), actual: []ActualBill{bill("1", vest, k)}, in: map[string]int{"1": 99}, keep: []string{"1"}, out: map[string]int{}},
			reconcileCase{name: "excluded " + string(k) + " never removed as a duplicate", declared: want(vest), actual: []ActualBill{bill("1", vest, LedgerProduction), bill("2", vest, k)}, in: map[string]int{"2": 99}, keep: []string{"1", "2"}, out: map[string]int{}},
			reconcileCase{name: "excluded " + string(k) + " satisfies its declared order", declared: want(vest), actual: []ActualBill{bill("1", vest, k)}, keep: []string{"1"}, out: map[string]int{}},
			reconcileCase{name: "spent excluded " + string(k) + " is kept and does not satisfy", declared: want(vest), actual: []ActualBill{{ID: "1", Bench: "b1", Kind: k, Spec: vest, Spent: true}}, in: map[string]int{"1": 99}, place: []OrderSpec{vest}, keep: []string{"1"}, out: map[string]int{}},
		)
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := ReconcileLedger(tc.declared, tc.actual, tc.in, tc.copies)
			ids := func(bs []ActualBill) []string {
				var out []string
				for _, b := range bs {
					out = append(out, b.ID)
				}
				return out
			}
			if !reflect.DeepEqual(got.Place, tc.place) || !reflect.DeepEqual(ids(got.Remove), tc.remove) || !reflect.DeepEqual(ids(got.Keep), tc.keep) || !reflect.DeepEqual(got.Orphans, tc.out) {
				t.Fatalf("got place=%v remove=%v keep=%v orphans=%v", got.Place, ids(got.Remove), ids(got.Keep), got.Orphans)
			}
		})
	}
}

// An undeclared bill survives until it has been undeclared for the whole
// grace period of consecutive Rounds, feeding each Round's counters forward.
func TestReconcileLedgerGraceAcrossRounds(t *testing.T) {
	actual := []ActualBill{{ID: "1", Spec: ledgerSpec("vest")}}
	counters := map[string]int(nil)
	for round := 1; round < OrphanGraceRounds; round++ {
		plan := ReconcileLedger(nil, actual, counters, nil)
		if len(plan.Remove) != 0 {
			t.Fatalf("round %d removed early", round)
		}
		counters = plan.Orphans
	}
	if plan := ReconcileLedger(nil, actual, counters, nil); len(plan.Remove) != 1 {
		t.Fatalf("not removed after grace: %+v", plan)
	}
}
