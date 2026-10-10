package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// For owns the orders and names the concern on the abstains without touching
// the receiver.
func TestDeclaredForStampsOwnerAndConcern(t *testing.T) {
	d := Declared{Orders: []OrderSpec{{Recipe: "A"}, {Recipe: "B", Owner: MaintainArt}}}
	d.Unread(CauseUnreadStock)
	d.Unread(CauseUnreadStock)
	got := d.For(MaintainResource)
	if got.Orders[0].Owner != MaintainResource || got.Orders[1].Owner != MaintainArt || len(got.Abstains) != 1 || got.Abstains[0].Concern != MaintainResource {
		t.Fatalf("for = %+v", got)
	}
	if d.Orders[0].Owner != "" || d.Abstains[0].Concern != "" {
		t.Fatalf("receiver changed: %+v", d)
	}
}

// Two concerns declaring one spec coalesce into one order that lists both.
func TestCoalescedSpecListsEveryOwner(t *testing.T) {
	shared := viewOrder("Make_Meal")
	declared := []Declared{
		Declared{Orders: []OrderSpec{shared}}.For(EnsureCooking),
		Declared{Orders: []OrderSpec{shared, viewOrder("Make_Pemmican")}}.For(MaintainFoodStorage),
	}
	v := BuildLedgerView(LedgerViewInput{Declarers: []NamedDeclared{{Name: "Cook", Declared: declared[0]}, {Name: "Store", Declared: declared[1]}}, Plan: ReconcileLedger(declared, nil, nil, nil)})
	owners := map[string][]string{}
	for _, o := range v.Orders {
		owners[o.Recipe] = o.Owners
	}
	if len(v.Orders) != 2 || !slices.Equal(owners["Make_Meal"], []string{"EnsureCooking", "MaintainFoodStorage"}) || !slices.Equal(owners["Make_Pemmican"], []string{"MaintainFoodStorage"}) {
		t.Fatalf("orders = %+v", v.Orders)
	}
}

// A declarer serving several concerns that abstains for one still shows the
// other concerns' orders, names the one that abstained and the fact it lacked,
// and the Round removes nothing.
func TestOneConcernAbstainingStillShowsTheOthersAndRemovesNothing(t *testing.T) {
	var multi Declared
	multi.Merge(Declared{Orders: []OrderSpec{viewOrder("Make_Arm")}}.For(MaintainSurgery))
	multi.Merge(Abstaining(CauseUnreadBabyFeeding).For(MaintainBabyFeeding))
	orphan := ActualBill{ID: "B_old", Bench: "T1", Spec: viewOrder("Make_Old")}
	declared := []Declared{multi}
	plan := ReconcileLedger(declared, []ActualBill{orphan}, map[string]int{"B_old": OrphanGraceRounds - 1}, nil)
	if len(plan.Remove) != 0 || len(plan.Held) != 1 || len(plan.Place) != 1 {
		t.Fatalf("plan = %+v", plan)
	}
	v := BuildLedgerView(LedgerViewInput{Declarers: []NamedDeclared{{Name: "Care", Declared: multi}}, Plan: plan})
	if len(v.Orders) != 1 || v.Orders[0].Owners[0] != "MaintainSurgery" || !v.Abstained || v.Orphans[0].State != OrphanHeld {
		t.Fatalf("view = %+v", v)
	}
	if a := v.Declarers[0].Abstains; len(a) != 1 || a[0].Concern != "MaintainBabyFeeding" || a[0].Fact != CauseUnreadBabyFeeding {
		t.Fatalf("abstains = %+v", a)
	}
}

// The latest attempt per order wins, and the view carries it without touching
// the stored rows.
func TestViewWithAttemptsKeepsTheLatest(t *testing.T) {
	order := viewOrder("Make_Vest")
	declared := []Declared{{Orders: []OrderSpec{order}}}
	view := BuildLedgerView(LedgerViewInput{Declarers: []NamedDeclared{{Name: "x", Declared: declared[0]}}, Plan: ReconcileLedger(declared, nil, nil, nil)})
	attempts := PlacementAttempts{}
	attempts.Note(order.Key(), PlacementAttempt{Plan: "p1", Tick: 10, Outcome: AttemptRefused, Reason: "bench_bill_slots_full"})
	attempts.Note(order.Key(), PlacementAttempt{Plan: "p2", Tick: 20, Outcome: AttemptAccepted})
	attempts.Note(order.Key(), PlacementAttempt{Plan: "p0", Tick: 5, Outcome: AttemptRefused})
	read := view.WithAttempts(attempts)
	if !read.AttemptsKnown || read.Orders[0].Attempt == nil || read.Orders[0].Attempt.Plan != "p2" {
		t.Fatalf("read = %+v", read.Orders)
	}
	if view.AttemptsKnown || view.Orders[0].Attempt != nil {
		t.Fatalf("the stored view changed: %+v", view.Orders)
	}
}

// Candidates that never reach an order are counted by reason and the view
// lists them in a fixed order.
func TestExportCountsDroppedCandidatesByReason(t *testing.T) {
	// No reachable buyer.
	noBuyer := exportFixture(exportProfile("w1", 10))
	noBuyer.Buys["Jewel"], noBuyer.Buys["WoodTrinket"] = domain.Known(false), domain.Known(true)
	if plan := DeclareExportOrders(noBuyer); plan.Dropped[ExportDropNoBuyer] == 0 || plan.Dropped[ExportDropNoIngredient] != 0 {
		t.Fatalf("dropped = %+v", plan.Dropped)
	}
	// No usable ingredient.
	noIngredient := exportFixture(exportProfile("w1", 10))
	noIngredient.Supply[1].Available = domain.Known[int64](5)
	if plan := DeclareExportOrders(noIngredient); plan.Dropped[ExportDropNoIngredient] == 0 {
		t.Fatalf("dropped = %+v", plan.Dropped)
	}
	// Margin at or below zero: an unpriced ingredient cannot be netted.
	noMargin := exportFixture(exportProfile("w1", 10))
	delete(noMargin.Items.Market, "Steel")
	if plan := DeclareExportOrders(noMargin); plan.Dropped[ExportDropNoMargin] == 0 {
		t.Fatalf("dropped = %+v", plan.Dropped)
	}
	// Runway guard.
	guard := exportFixture(exportProfile("w1", 10), exportProfile("w2", 10))
	guard.Supply[0].Available = domain.Known[int64](0)
	guard.Supply[1].Available = domain.Known[int64](100)
	guard.Runways = []ResourceRunway{{Resource: "WoodLog", Stock: domain.Known[int64](100), ConsumptionPerDay: domain.Known(4.0)}}
	plan := DeclareExportOrders(guard)
	if plan.Dropped[ExportDropRunway] == 0 {
		t.Fatalf("dropped = %+v", plan.Dropped)
	}
	view := NewExportView(plan, domain.Known(500.0))
	if len(view.Dropped) == 0 || view.Dropped[len(view.Dropped)-1].Reason != ExportDropRunway || view.Dropped[len(view.Dropped)-1].Count != plan.Dropped[ExportDropRunway] {
		t.Fatalf("view = %+v", view.Dropped)
	}
	// Nothing dropped is an empty list, not null.
	if clean := NewExportView(DeclareExportOrders(exportFixture(exportProfile("w1", 10))), domain.Known(500.0)); clean.Dropped == nil {
		t.Fatal("dropped must be an empty list")
	}
}
