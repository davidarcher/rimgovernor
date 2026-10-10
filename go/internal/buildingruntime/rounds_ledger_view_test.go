package buildingruntime

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The ledger view projects the Round's memory: a placed order, an orphan
// counting its grace down and an order no bench can take with its unmet row.
func TestWorkLedgerViewShowsPlacedOrphanAndUnmet(t *testing.T) {
	f := newLedgerFixture(t)
	if v := f.reviewer.WorkLedgerView(); v.Status != policy.LedgerViewNone || v.Orders == nil || v.Orphans == nil {
		t.Fatalf("before any review: %+v", v)
	}
	vest := ledgerOrder("Make_Vest")
	smithy := policy.OrderSpec{Recipe: "Make_Pike", Mode: domain.StockTarget, Target: 30, BenchKind: "FueledSmithy", Product: "Steel", Class: policy.ResourceMaterial}
	f.declare(vest, smithy)
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_Vest", vest), fakeBill("Bill_Hat", ledgerOrder("Make_Hat")))}
	f.round()
	v := f.reviewer.WorkLedgerView()
	if v.Status != policy.LedgerViewReconciled || v.GraceRounds != policy.OrphanGraceRounds || v.Abstained {
		t.Fatalf("view = %+v", v)
	}
	orders := map[string]policy.LedgerOrderView{}
	for _, o := range v.Orders {
		orders[o.Recipe] = o
	}
	if o := orders["Make_Vest"]; o.State != policy.OrderPlaced || len(o.Benches) != 1 || o.Benches[0] != ledgerBench || o.Target != 2 || o.Mode != string(domain.GearBatch) || strings.Join(o.Owners, ",") != "MaintainResource" {
		t.Fatalf("vest = %+v", o)
	}
	if o := orders["Make_Pike"]; o.State != policy.OrderUnplaced || o.Reason != policy.UnmetNoBench || o.Unplaced != 1 || o.BenchKind != "FueledSmithy" {
		t.Fatalf("pike = %+v", o)
	}
	if len(v.Orphans) != 1 || v.Orphans[0].ID != "Bill_Hat" || v.Orphans[0].State != policy.OrphanPending || v.Orphans[0].GraceLeft != policy.OrphanGraceRounds-1 {
		t.Fatalf("orphans = %+v", v.Orphans)
	}
	if len(v.Unmet) != 1 || v.Unmet[0].BenchKind != "FueledSmithy" || v.Unmet[0].Reason != policy.UnmetNoBench {
		t.Fatalf("unmet = %+v", v.Unmet)
	}
	// The grace counts down on the next Round.
	f.round()
	if o := f.reviewer.WorkLedgerView().Orphans; len(o) != 1 || o[0].GraceLeft != policy.OrphanGraceRounds-2 {
		t.Fatalf("orphans = %+v", o)
	}
}

// A planner that abstains holds the orphans and the view says so; an unread
// readback and an inactive ledger have their own statuses.
func TestWorkLedgerViewStatuses(t *testing.T) {
	f := newLedgerFixture(t)
	f.declarer.declared = policy.Abstaining(policy.CauseUnreadStock)
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_Hat", ledgerOrder("Make_Hat")))}
	f.round()
	v := f.reviewer.WorkLedgerView()
	if !v.Abstained || len(v.Declarers) != 1 || len(v.Declarers[0].Abstains) != 1 || len(v.Orphans) != 1 || v.Orphans[0].State != policy.OrphanHeld {
		t.Fatalf("view = %+v", v)
	}
	unread := ledgerBenchRow()
	unread.Bills = domain.Unknown[[]policy.GearBill]()
	f.native.benches = []policy.GearBench{unread}
	f.round()
	if v := f.reviewer.WorkLedgerView(); v.Status != policy.LedgerViewUnread || len(v.Declarers) != 1 {
		t.Fatalf("unread view = %+v", v)
	}
	f.reviewer.ledger.declarers = nil
	f.round()
	if v := f.reviewer.WorkLedgerView(); v.Status != policy.LedgerViewInactive {
		t.Fatalf("inactive view = %+v", v)
	}
}

// The Ledger view reads each order's latest placement outcome from the journal: an
// accepted bill, a bill native refused (with its reason) and an order no plan
// ever carried.
func TestWorkLedgerShowsPlacementOutcomesFromJournal(t *testing.T) {
	f := newLedgerFixture(t)
	ctx := context.Background()
	f.declare(ledgerOrder("Make_Vest"), ledgerOrder("Make_Hat"), ledgerOrder("Make_Cap"))
	f.native.benches = []policy.GearBench{ledgerBenchRow()}
	result := f.round()
	loaded, err := f.db.LoadPlan(ctx, result.Plan)
	if err != nil || len(loaded.Progress) != 2 {
		t.Fatalf("plan = %+v err %v", loaded.Progress, err)
	}
	scope := f.session.State().Snapshot
	scope.Plan, scope.Revision = result.Plan, 1
	for _, progress := range loaded.Progress {
		action := progress.Action().ID()
		if _, err = f.db.Prepare(ctx, result.Plan, action, scope, 7); err != nil {
			t.Fatal(err)
		}
		dispatched, err := f.db.Dispatch(ctx, result.Plan, action, scope, 7)
		if err != nil {
			t.Fatal(err)
		}
		attempt := dispatched.View().Attempt
		if placedBill(t, progress.Action()).Recipe() == "Make_Vest" {
			_, err = f.db.RecordBillReceipt(ctx, result.Plan, action, attempt, "Bill_Vest")
		} else {
			_, err = f.db.RecordReceipts(ctx, []store.BatchReceipt{{Plan: result.Plan, Action: action, Attempt: attempt, Receipt: domain.ReceiptRefused,
				Refusal: &domain.NativeRefusal{Code: "bill_slots", Reason: "bench_bill_slots_full", Class: domain.RefusalTransient}}})
		}
		if err != nil {
			t.Fatal(err)
		}
	}
	// A restart: the index is empty and unseeded, so the journal's receipts seed it.
	index := &f.reviewer.ledger.attempts
	index.entries, index.seeded = nil, false
	v := f.reviewer.WorkLedger(ctx)
	if !v.AttemptsKnown {
		t.Fatalf("journal unread: %+v", v)
	}
	attempt := map[string]*policy.PlacementAttempt{}
	for _, o := range v.Orders {
		attempt[o.Recipe] = o.Attempt
	}
	if a := attempt["Make_Vest"]; a == nil || a.Outcome != policy.AttemptAccepted || a.Tick != 7 {
		t.Fatalf("vest = %+v", a)
	}
	if a := attempt["Make_Hat"]; a == nil || a.Outcome != policy.AttemptRefused || a.Reason != "bench_bill_slots_full" || a.Code != "bill_slots" {
		t.Fatalf("hat = %+v", a)
	}
	if a, ok := attempt["Make_Cap"]; !ok || a != nil {
		t.Fatalf("cap was never carried by a plan: %+v", a)
	}
	// The worker's next outcome for the refused bill replaces the seeded one.
	for _, progress := range loaded.Progress {
		if placedBill(t, progress.Action()).Recipe() == "Make_Hat" {
			f.reviewer.notePlacement(result.Plan, progress.Action(), domain.ProgressView{Tick: 9, Receipt: domain.Known(domain.ReceiptUnknown)})
		}
	}
	for _, o := range f.reviewer.WorkLedger(ctx).Orders {
		if o.Recipe == "Make_Hat" && (o.Attempt == nil || o.Attempt.Outcome != policy.AttemptUnconfirmed || o.Attempt.Tick != 9) {
			t.Fatalf("hat after the worker noted it = %+v", o.Attempt)
		}
	}
	if f.reviewer.WorkLedgerView().AttemptsKnown {
		t.Fatal("the memory view must not claim a journal read")
	}
}
