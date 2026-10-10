package main

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// sampleLedger is a Round with a placed order, an orphan counting down its
// grace, an order no bench can take, a surgery bill and an open silver gap.
func sampleLedger() policy.LedgerView {
	steel := policy.OrderSpec{Recipe: "Smelt_Steel", Mode: domain.StockTarget, Target: 100, BenchKind: "ElectricSmelter", Product: "Steel", Class: policy.ResourceMaterial}
	pike := policy.OrderSpec{Recipe: "Make_Pike", Mode: domain.GearBatch, Target: 3, BenchKind: "FueledSmithy", Worker: "Pawn_7", Ingredients: []string{"Steel"}}
	declared := []policy.Declared{{Orders: []policy.OrderSpec{steel, pike}}}
	actual := []policy.ActualBill{
		{ID: "Bill_1", Bench: "Smelter_1", Spec: steel},
		{ID: "Bill_9", Bench: "Smelter_1", Spec: policy.OrderSpec{Recipe: "Make_Old", Mode: domain.GearBatch, Target: 1, BenchKind: "ElectricSmelter"}},
		{ID: "Bill_3", Bench: "Smelter_1", Kind: policy.LedgerSurgery, Spec: policy.OrderSpec{Recipe: "Make_Bionic", Mode: domain.GearBatch, Target: 1, BenchKind: "ElectricSmelter"}},
	}
	plan := policy.ReconcileLedger(declared, actual, map[string]int{"Bill_9": 1}, nil)
	placed, unplaced := policy.PlaceLedgerOrders(plan.Place, nil)
	v := policy.BuildLedgerView(policy.LedgerViewInput{Tick: 61000, Declarers: []policy.NamedDeclared{{Name: "Resource", Declared: declared[0]}}, Plan: plan, Placed: placed, Unplaced: unplaced,
		Dispatch: map[string]policy.OrderDispatch{steel.Key(): {Copies: 1, Eligible: 1}},
		Memory:   policy.DispatchMemory{Windows: map[string]policy.ThroughputWindow{steel.Key(): {Open: true, Start: 60000, StartStock: 20, Deficit: 80, PerDay: 40, Carrying: 1, Samples: 3, Busy: 2}}},
		Stock:    domain.Known(map[policy.Resource]int64{"Steel": 31}),
		Unmet:    []policy.UnmetThroughput{{BenchKind: "FueledSmithy", ShortPerDay: 2.5, Reason: policy.UnmetNoBench}}, Further: []string{"FueledSmithy"}})
	v.Export = policy.NewExportView(policy.ExportPlan{InFlight: 120, Candidates: []policy.ExportCandidate{{Recipe: "Make_Sculpture", BenchKind: "Stonecutter", Product: "SculptureSmall", Stuff: "BlocksGranite", Worker: "Pawn_2", Score: 0.0123, Net: 210, Ticks: 17000}},
		Ranked: []policy.ExportCandidate{{Recipe: "Make_Sculpture", BenchKind: "Stonecutter", Product: "SculptureSmall", Stuff: "BlocksGranite", Worker: "Pawn_2"}}}, domain.Known(400.0))
	return v
}

func tableText(s LedgerSection) string {
	var b strings.Builder
	for _, r := range s.Rows {
		b.WriteString(r.Flag + "|" + strings.Join(r.Cells, " | ") + "\n")
	}
	return b.String()
}

func section(t *testing.T, p LedgerPage, title string) LedgerSection {
	t.Helper()
	for _, s := range p.Sections {
		if s.Title == title {
			return s
		}
	}
	t.Fatalf("no section %q", title)
	return LedgerSection{}
}

func TestLedgerPageShowsPlacedOrphanAndUnmetRows(t *testing.T) {
	v := sampleLedger()
	p := ledgerPage(Reading[policy.LedgerView]{Value: &v})
	if !p.HasValue || !strings.Contains(p.Headline, "2 wanted order(s), 1 orphan bill(s)") {
		t.Fatalf("headline %q", p.Headline)
	}
	orders := tableText(section(t, p, "Declared orders"))
	for _, want := range []string{"Resource | Smelt_Steel (Steel) | 100 | stock target | ElectricSmelter | - | - | placed | Smelter_1",
		"warn|Resource | Make_Pike | 3 | finite batch | FueledSmithy | Pawn_7 | Steel | unplaced x1: no bench for it (no_bench) | -"} {
		if !strings.Contains(orders, want) {
			t.Fatalf("orders missing %q:\n%s", want, orders)
		}
	}
	if got := tableText(section(t, p, "Orphan bills")); !strings.Contains(got, "Smelter_1 | Make_Old | 1 | - | undeclared 2 of 3 Rounds: 1 left before removal") {
		t.Fatalf("orphans:\n%s", got)
	}
	if got := tableText(section(t, p, "Unmet throughput")); !strings.Contains(got, "FueledSmithy | 2.5/day | no bench for it (no_bench) | requested") {
		t.Fatalf("unmet:\n%s", got)
	}
	if got := tableText(section(t, p, "Calibration windows")); !strings.Contains(got, "Smelt_Steel | 40/day | +11 since stock 20 | tick 60,000 to 120,000, 3 sample(s) | 1") {
		t.Fatalf("windows:\n%s", got)
	}
	if got := tableText(section(t, p, "Never removed")); !strings.Contains(got, "Make_Bionic | surgery") {
		t.Fatalf("excluded:\n%s", got)
	}
	export := section(t, p, "Silver gap and export candidates")
	if !strings.Contains(export.Note, "Silver gap 400 silver; 120 silver already in flight") || !strings.Contains(tableText(export), "Make_Sculpture | SculptureSmall | BlocksGranite | Pawn_2 | 0.012 | 210 silver | 17,000 | yes") {
		t.Fatalf("export: %q\n%s", export.Note, tableText(export))
	}
}

func TestLedgerPageFeedStates(t *testing.T) {
	if p := ledgerPage(Reading[policy.LedgerView]{NotServed: true}); p.HasValue || !strings.Contains(p.Notice, "not served") {
		t.Fatalf("%+v", p.Feed)
	}
	none := policy.NewLedgerView(policy.LedgerViewNone, 0)
	p := ledgerPage(Reading[policy.LedgerView]{Value: &none})
	if !p.HasValue || !strings.Contains(p.Headline, "No review") {
		t.Fatalf("%q", p.Headline)
	}
	for _, s := range p.Sections {
		if s.Note == "" && len(s.Rows) == 0 {
			t.Fatalf("%q is empty without a note", s.Title)
		}
	}
}
