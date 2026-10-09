package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDeclareSelection(t *testing.T) {
	benches := []GearBench{{ID: "b1", Def: "TableMachining"}, {ID: "b2"}}
	sel := BillSelection{Bench: "b1", Recipe: "Make_Leg", Mode: domain.GearBatch, Target: 1}
	want := OrderSpec{Recipe: "Make_Leg", Mode: domain.GearBatch, Target: 1, BenchKind: "TableMachining"}
	for name, c := range map[string]struct {
		sel      BillSelection
		selected bool
		want     Declared
	}{
		"selected bill is declared":      {sel, true, Declared{Orders: []OrderSpec{want}}},
		"nothing selected declares none": {sel, false, Declared{}},
		"bench missing abstains":         {BillSelection{Bench: "b9", Recipe: "r"}, true, Declared{Abstain: true}},
		"bench without a def abstains":   {BillSelection{Bench: "b2", Recipe: "r"}, true, Declared{Abstain: true}},
	} {
		if got := DeclareSelection(c.sel, c.selected, benches); !reflect.DeepEqual(got, c.want) {
			t.Errorf("%s: got %+v, want %+v", name, got, c.want)
		}
	}
}

func TestDeclareMedicine(t *testing.T) {
	benches := []GearBench{{ID: "b1", Def: "HerbalBench"}}
	produce := MedicineMethod{Kind: MedicineProduce, Bench: "b1", Recipe: "Make_Herbal", Target: 12}
	want := Declared{Orders: []OrderSpec{{Recipe: "Make_Herbal", Mode: domain.StockTarget, Target: 12, BenchKind: "HerbalBench"}}}
	if got := DeclareMedicine(produce, benches); !reflect.DeepEqual(got, want) {
		t.Errorf("produce: got %+v, want %+v", got, want)
	}
	if got := DeclareMedicine(MedicineMethod{Kind: MedicineUnknown}, benches); !got.Abstain {
		t.Errorf("unknown must abstain: %+v", got)
	}
	for _, kind := range []MedicineMethodKind{MedicineRecovered, MedicineWait, MedicineBlocked} {
		if got := DeclareMedicine(MedicineMethod{Kind: kind}, benches); got.Abstain || len(got.Orders) != 0 {
			t.Errorf("%s declares nothing: %+v", kind, got)
		}
	}
}

// A bill of a declare-only kind is never an orphan, whoever placed it and
// however long it has gone undeclared, and an unmigrated owner's bill is kept
// whatever its kind: removal is only ever for a migrated owner's production
// bill.
func TestDeclareOnlyBillsAreNeverRemoved(t *testing.T) {
	spec := OrderSpec{Recipe: "Make_X", Mode: domain.StockTarget, Target: 3, BenchKind: "B"}
	kinds := []LedgerBillKind{LedgerMechGestation, LedgerSurgery, LedgerMedical, LedgerBabyFood}
	var actual []ActualBill
	orphans := map[string]int{}
	for i, k := range kinds {
		id := string(k)
		actual = append(actual, ActualBill{ID: id, Bench: "b", Kind: k, Spec: spec, Migrated: true}, ActualBill{ID: id + "-spent", Bench: "b", Kind: k, Spec: spec, Migrated: true, Spent: true})
		orphans[id], orphans[id+"-spent"] = OrphanGraceRounds*10+i, OrphanGraceRounds*10
	}
	actual = append(actual, ActualBill{ID: "legacy", Bench: "b", Spec: spec}, ActualBill{ID: "ledger", Bench: "b", Spec: spec, Migrated: true})
	orphans["legacy"], orphans["ledger"] = OrphanGraceRounds*10, OrphanGraceRounds*10
	for rounds := 0; rounds < 2*OrphanGraceRounds; rounds++ {
		plan := ReconcileLedger(nil, actual, orphans, nil)
		for _, b := range plan.Remove {
			if b.ID != "ledger" {
				t.Fatalf("round %d removed %s (%s), only a migrated production orphan may go", rounds, b.ID, b.Kind)
			}
		}
		if rounds == 0 && len(plan.Remove) != 1 {
			t.Fatalf("the control (a migrated production orphan) was not removed: %+v", plan.Remove)
		}
		orphans = plan.Orphans
	}
}
