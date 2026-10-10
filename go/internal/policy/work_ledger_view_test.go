package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func viewOrder(recipe string) OrderSpec {
	return OrderSpec{Recipe: recipe, Mode: domain.StockTarget, Target: 40, BenchKind: "TableMachining", Product: "Steel", Class: ResourceMaterial}
}

// Every state of the view in one Round: a kept order, an order being placed,
// one that fits no bench, the orphans (counting, removed, held) and an excluded
// kind.
func TestBuildLedgerViewStates(t *testing.T) {
	kept, placing, noBench := viewOrder("Make_A"), viewOrder("Make_B"), viewOrder("Make_C")
	noBench.BenchKind = "FueledSmithy"
	counting := ActualBill{ID: "B_count", Bench: "T1", Spec: viewOrder("Make_Old")}
	removed := ActualBill{ID: "B_gone", Bench: "T1", Spec: viewOrder("Make_Gone")}
	surgery := ActualBill{ID: "B_surg", Bench: "T1", Kind: LedgerSurgery, Spec: viewOrder("Make_Arm")}
	actual := []ActualBill{{ID: "B_kept", Bench: "T1", Spec: kept}, counting, removed, surgery}
	declared := []Declared{Declared{Orders: []OrderSpec{kept, placing, noBench}}.For(MaintainResource)}
	plan := ReconcileLedger(declared, actual, map[string]int{"B_count": 1, "B_gone": OrphanGraceRounds - 1}, nil)
	placed, unplaced := PlaceLedgerOrders(plan.Place, []GearBench{{ID: "T1", Def: "TableMachining", Usable: domain.Known(true),
		Recipes: domain.Known([]GearRecipe{{Definition: "Make_B", AvailableOn: domain.Known(true)}}), Bills: domain.Known([]GearBill{})}})
	memory := DispatchMemory{Widen: map[string]int{kept.Key(): 1},
		Shortfalls: map[string]UnmetThroughput{kept.Key(): {BenchKind: "TableMachining", ShortPerDay: 12, Reason: UnmetIngredients}},
		Windows:    map[string]ThroughputWindow{kept.Key(): {Open: true, Start: 1000, StartStock: 10, Deficit: 30, PerDay: 20, Carrying: 1, Samples: 4, Busy: 3}}}
	v := BuildLedgerView(LedgerViewInput{Tick: 5000, Declarers: []NamedDeclared{{Name: "Resource", Declared: declared[0]}}, Plan: plan, Placed: placed, Unplaced: unplaced,
		Dispatch: map[string]OrderDispatch{noBench.Key(): {Copies: 1}}, Memory: memory, Stock: domain.Known(map[Resource]int64{"Steel": 25}),
		Unmet: []UnmetThroughput{{BenchKind: "FueledSmithy", ShortPerDay: 3, Reason: UnmetNoBench}}, Further: []string{"FueledSmithy"}})

	states := map[string]LedgerOrderView{}
	for _, o := range v.Orders {
		states[o.Recipe] = o
	}
	if o := states["Make_A"]; o.State != OrderPlaced || len(o.Benches) != 1 || strings.Join(o.Owners, ",") != "MaintainResource" || o.Widen != 1 || o.Shortfall == nil || o.Shortfall.Reason != UnmetIngredients {
		t.Fatalf("kept = %+v", o)
	} else if w := o.Window; w == nil || w.ObservedGain == nil || *w.ObservedGain != 15 || w.PredictedPerDay != 20 || w.Ends != 1000+ThroughputWindowTicks {
		t.Fatalf("window = %+v", w)
	}
	if o := states["Make_B"]; o.State != OrderPlacing || len(o.Placing) != 1 || o.Placing[0] != "T1" {
		t.Fatalf("placing = %+v", o)
	}
	if o := states["Make_C"]; o.State != OrderUnplaced || o.Reason != UnmetNoBench || o.Unplaced != 1 {
		t.Fatalf("unplaced = %+v", o)
	}
	byID := map[string]LedgerBillView{}
	for _, b := range v.Orphans {
		byID[b.ID] = b
	}
	if b := byID["B_count"]; b.State != OrphanPending || b.Rounds != 2 || b.GraceLeft != OrphanGraceRounds-2 {
		t.Fatalf("counting = %+v", b)
	}
	if b := byID["B_gone"]; b.State != OrphanRemoving || b.GraceLeft != 0 {
		t.Fatalf("removed = %+v", b)
	}
	if len(v.Orphans) != 2 || len(v.Excluded) != 1 || v.Excluded[0].Kind != string(LedgerSurgery) {
		t.Fatalf("orphans %+v excluded %+v", v.Orphans, v.Excluded)
	}
	if len(v.Unmet) != 1 || v.Unmet[0].Reason != UnmetNoBench || len(v.FurtherBenches) != 1 || v.Abstained {
		t.Fatalf("unmet %+v further %+v", v.Unmet, v.FurtherBenches)
	}
}

// An abstaining planner holds every orphan, and the view names who abstained.
func TestBuildLedgerViewAbstainHoldsOrphans(t *testing.T) {
	orphan := ActualBill{ID: "B_1", Bench: "T1", Spec: viewOrder("Make_Old")}
	declared := []Declared{{Abstains: []Abstain{{Fact: UnreadStock}}}}
	plan := ReconcileLedger(declared, []ActualBill{orphan}, map[string]int{"B_1": 2}, nil)
	v := BuildLedgerView(LedgerViewInput{Declarers: []NamedDeclared{{Name: "Gear", Declared: declared[0]}}, Plan: plan})
	if !v.Abstained || len(v.Declarers[0].Abstains) != 1 || len(v.Orphans) != 1 || v.Orphans[0].State != OrphanHeld {
		t.Fatalf("view = %+v", v)
	}
}

// The export view nets what is in flight off the gap and marks the ordered
// candidates, keeping an ordered one the cut would drop.
func TestNewExportView(t *testing.T) {
	a := ExportCandidate{Recipe: "Make_A", BenchKind: "T", Product: "Steel", Worker: "p1", Score: 2}
	b := ExportCandidate{Recipe: "Make_B", BenchKind: "T", Product: "Gold", Worker: "p2", Score: 1}
	v := NewExportView(ExportPlan{InFlight: 30, Candidates: []ExportCandidate{a}, Ranked: []ExportCandidate{b}}, domain.Known(100.0))
	if !v.GapKnown || v.Gap != 100 || v.Remaining != 70 || len(v.Candidates) != 2 || v.Candidates[0].Ordered || !v.Candidates[1].Ordered {
		t.Fatalf("view = %+v", v)
	}
	if u := NewExportView(ExportPlan{}, domain.Unknown[float64]()); u.GapKnown || u.Remaining != 0 {
		t.Fatalf("unknown = %+v", u)
	}
}
