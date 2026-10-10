package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
	if o := orders["Make_Vest"]; o.State != policy.OrderPlaced || len(o.Benches) != 1 || o.Benches[0] != ledgerBench || o.Target != 2 || o.Mode != string(domain.GearBatch) || o.Owner != "fake" {
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
	f.declarer.declared = policy.Declared{Abstain: true}
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_Hat", ledgerOrder("Make_Hat")))}
	f.round()
	v := f.reviewer.WorkLedgerView()
	if !v.Abstained || len(v.Declarers) != 1 || !v.Declarers[0].Abstain || len(v.Orphans) != 1 || v.Orphans[0].State != policy.OrphanHeld {
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
