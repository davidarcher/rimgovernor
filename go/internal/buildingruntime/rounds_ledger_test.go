package buildingruntime

import (
	"context"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// ledgerNative is the colony fixture with a settable bench census.
type ledgerNative struct {
	*roundsNative
	benches []policy.GearBench
}

func (n *ledgerNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	rows := make([]bridge.GearBenchRead, 0, len(n.benches))
	for _, b := range n.benches {
		rows = append(rows, bridge.GearBenchRead{Token: "token-" + b.ID, Bench: b})
	}
	return rows, bridge.Result{}, nil
}

// fakeDeclarer is the test double standing in for a migrated bill planner.
type fakeDeclarer struct{ declared policy.Declared }

func (f *fakeDeclarer) DeclareOrders(context.Context, domain.GenerationSnapshot, observation.ColonyProjection, []policy.GearBench) (policy.Declared, error) {
	return f.declared, nil
}

const ledgerBench = "Bench_1"

func ledgerOrder(recipe string) policy.OrderSpec {
	return policy.OrderSpec{Recipe: recipe, Mode: domain.GearBatch, Target: 2, BenchKind: "TableMachining"}
}

func fakeBill(id string, spec policy.OrderSpec) policy.GearBill {
	return policy.GearBill{ID: id, Recipe: spec.Recipe, Spec: domain.Known(spec)}
}

func ledgerBenchRow(bills ...policy.GearBill) policy.GearBench {
	return policy.GearBench{
		ID: ledgerBench, Def: "TableMachining", Usable: domain.Known(true), Bills: domain.Known(bills),
		Recipes: domain.Known([]policy.GearRecipe{
			{Definition: "Make_Vest", AvailableOn: domain.Known(true)},
			{Definition: "Make_Hat", AvailableOn: domain.Known(true)},
		}),
	}
}

type ledgerFixture struct {
	t        *testing.T
	reviewer *Rounder
	planner  *RoundsLedgerPlanner
	db       *store.Store
	session  *playerFakeSession
	native   *ledgerNative
	declarer *fakeDeclarer
}

func newLedgerFixture(t *testing.T) *ledgerFixture {
	t.Helper()
	p, db, session, _ := playerFixture(t)
	request := playerAcquire(t, p)
	if _, err := p.Resume(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	f := &ledgerFixture{t: t, db: db, session: session, native: &ledgerNative{roundsNative: colonyCoreNative(t)}, declarer: &fakeDeclarer{}}
	f.restart(p)
	return f
}

// restart is a fresh Rounder over the same journal: the ledger memory is empty.
func (f *ledgerFixture) restart(p *Player) {
	f.t.Helper()
	reviewer, err := NewRounder(p, f.native, testkit.NewManualClock(time.Now()), policy.DefaultRoundsPolicy(), time.Second)
	if err != nil {
		f.t.Fatal(err)
	}
	if centre := f.native.reply.GetObserved().GetCenter(); centre != nil {
		centreOn(reviewer, domain.Cell{X: centre.GetX(), Z: centre.GetZ()})
	}
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainWorkLedger})
	reviewer.AddOrderDeclarer(f.declarer)
	if f.planner, err = NewRoundsLedgerPlanner(reviewer); err != nil {
		f.t.Fatal(err)
	}
	f.reviewer = reviewer
}

// round is one review and the ledger planner's step after it.
func (f *ledgerFixture) round() RoundsLedgerResult {
	f.t.Helper()
	ctx := context.Background()
	if _, err := f.reviewer.Step(ctx); err != nil {
		f.t.Fatal(err)
	}
	call, epoch, done, err := f.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		f.t.Fatal(err)
	}
	defer done()
	result, err := f.planner.step(call, epoch, newStepArbiter())
	if err != nil {
		f.t.Fatal(err)
	}
	return result
}

// settle completes the plan's actions the way an accepted receipt would, for
// the purpose of the next commit: nothing stays open.
func (f *ledgerFixture) settle(plan domain.PlanID) []domain.Action {
	f.t.Helper()
	loaded, err := f.db.LoadPlan(context.Background(), plan)
	if err != nil {
		f.t.Fatal(err)
	}
	var actions []domain.Action
	for _, progress := range loaded.Progress {
		actions = append(actions, progress.Action())
		if _, err := f.db.Cancel(context.Background(), plan, progress.Action().ID()); err != nil {
			f.t.Fatal(err)
		}
	}
	return actions
}

// place completes the plan's first action the way an accepted receipt would,
// journaling the native bill id it placed: the record that makes the bill a
// migrated owner's (store.PlacedBills).
func (f *ledgerFixture) place(plan domain.PlanID, bill string) {
	f.t.Helper()
	ctx := context.Background()
	loaded, err := f.db.LoadPlan(ctx, plan)
	if err != nil {
		f.t.Fatal(err)
	}
	scope := f.session.State().Snapshot
	scope.Plan, scope.Revision = plan, 1
	action := loaded.Progress[0].Action().ID()
	if _, err = f.db.Prepare(ctx, plan, action, scope, 1); err != nil {
		f.t.Fatal(err)
	}
	progress, err := f.db.Dispatch(ctx, plan, action, scope, 1)
	if err != nil {
		f.t.Fatal(err)
	}
	if _, err = f.db.RecordBillReceipt(ctx, plan, action, progress.View().Attempt, bill); err != nil {
		f.t.Fatal(err)
	}
}

func (f *ledgerFixture) declare(orders ...policy.OrderSpec) {
	f.declarer.declared = policy.Declared{Orders: orders}
}

func placedBill(t *testing.T, action domain.Action) domain.ProductionBill {
	t.Helper()
	bill, ok := action.ProductionBill()
	if !ok {
		t.Fatalf("not a production bill: %v", action)
	}
	return bill
}

// A stock target whose rate model is unread (no work hours in this fixture)
// still stands on one bench, and an order no bench of its kind can take, or
// every capable bench is at the slot cap for, is reported as unmet throughput
// in the Round facts for its bench kind.
func TestLedgerReportsUnmetThroughput(t *testing.T) {
	f := newLedgerFixture(t)
	stock := policy.OrderSpec{Recipe: "Make_Vest", Mode: domain.StockTarget, Target: 50, BenchKind: "TableMachining", Product: "WoodLog", Class: policy.ResourceMaterial}
	f.declare(stock)
	f.native.benches = []policy.GearBench{ledgerBenchRow()}
	result := f.round()
	if result.Plan == "" || len(f.settle(result.Plan)) != 1 {
		t.Fatalf("an unsized stock target must stand on one bench: %+v", result.Verdict)
	}
	if unmet := f.reviewer.ledgerUnmet(); len(unmet) != 0 {
		t.Fatalf("unmet = %+v", unmet)
	}
	// No bench of the kind.
	noBench := stock
	noBench.BenchKind = "FueledSmithy"
	f.declare(noBench)
	f.round()
	if unmet := f.reviewer.ledgerUnmet(); len(unmet) != 1 || unmet[0].BenchKind != "FueledSmithy" || unmet[0].Reason != policy.UnmetNoBench {
		t.Fatalf("unmet = %+v", unmet)
	}
	// The only capable bench carries 15 other bills.
	bills := make([]policy.GearBill, policy.LedgerBenchSlots)
	for i := range bills {
		hat := ledgerOrder("Make_Hat")
		hat.Target = int32(i + 1)
		bills[i] = fakeBill(fmt.Sprintf("Bill_%d", i), hat)
	}
	f.declare(stock)
	f.native.benches = []policy.GearBench{ledgerBenchRow(bills...)}
	f.round()
	if unmet := f.reviewer.ledgerUnmet(); len(unmet) != 1 || unmet[0].BenchKind != "TableMachining" || unmet[0].Reason != policy.UnmetSlotsFull {
		t.Fatalf("unmet = %+v", unmet)
	}
}

// A declared order with no bill is placed as one batched plan; once the bench
// carries it the ledger keeps it and commits nothing.
func TestLedgerPlacesThenKeeps(t *testing.T) {
	f := newLedgerFixture(t)
	vest := ledgerOrder("Make_Vest")
	f.declare(vest, ledgerOrder("Make_Hat"))
	f.native.benches = []policy.GearBench{ledgerBenchRow()}
	result := f.round()
	if result.Plan == "" {
		t.Fatalf("no plan committed: %+v", result.Verdict)
	}
	actions := f.settle(result.Plan)
	if len(actions) != 2 {
		t.Fatalf("actions = %d, want both orders in one plan", len(actions))
	}
	for _, action := range actions {
		if bill := placedBill(t, action); bill.Bench() != ledgerBench || bill.Mode() != domain.GearBatch || bill.Target() != 2 {
			t.Fatalf("bill = %+v", bill)
		}
	}
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_1", vest), fakeBill("Bill_2", ledgerOrder("Make_Hat")))}
	if again := f.round(); again.Plan != "" {
		t.Fatalf("a standing bill was committed again: %s", again.Plan)
	}
}

// Native lost a placed bill: the same order is placed again as a new attempt
// of the owner.
func TestLedgerReplacesALostBill(t *testing.T) {
	f := newLedgerFixture(t)
	f.declare(ledgerOrder("Make_Vest"))
	f.native.benches = []policy.GearBench{ledgerBenchRow()}
	first := f.round()
	if first.Plan == "" {
		t.Fatal("no first plan")
	}
	f.settle(first.Plan)
	second := f.round()
	if second.Plan == "" || second.Plan == first.Plan {
		t.Fatalf("lost bill not placed again: %q %q", second.Plan, first.Plan)
	}
}

// A bill no planner declared is removed only after the grace period, in a plan
// that also places what is missing; a restart empties the counters and only
// delays the removal.
func TestLedgerRemovesOrphansAfterGraceAndRebuildsOnRestart(t *testing.T) {
	f := newLedgerFixture(t)
	hat := ledgerOrder("Make_Hat")
	f.declare(hat)
	f.native.benches = []policy.GearBench{ledgerBenchRow()}
	f.place(f.round().Plan, "Bill_Hat")
	f.declare(ledgerOrder("Make_Vest"))
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_Hat", hat))}
	// The vest is placed at once; the orphan hat waits.
	first := f.round()
	if first.Plan == "" {
		t.Fatal("no placement")
	}
	for _, action := range f.settle(first.Plan) {
		if _, removal := action.RemoveProductionBill(); removal {
			t.Fatal("orphan removed before its grace")
		}
	}
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_Hat", hat), fakeBill("Bill_Vest", ledgerOrder("Make_Vest")))}
	// Rounds 2 and 3 of the orphan: nothing owed until the grace has run.
	for round := 2; round < policy.OrphanGraceRounds; round++ {
		if r := f.round(); r.Plan != "" {
			t.Fatalf("round %d committed %s", round, r.Plan)
		}
	}
	// A restart empties the counters.
	f.restart(f.reviewer.player)
	for round := 1; round < policy.OrphanGraceRounds; round++ {
		if r := f.round(); r.Plan != "" {
			t.Fatalf("restarted round %d committed %s", round, r.Plan)
		}
	}
	removal := f.round()
	if removal.Plan == "" {
		t.Fatalf("orphan not removed after grace: %+v", removal.Verdict)
	}
	actions := f.settle(removal.Plan)
	if len(actions) != 1 {
		t.Fatalf("actions = %d", len(actions))
	}
	if r, ok := actions[0].RemoveProductionBill(); !ok || r.Bench() != ledgerBench || r.Bill() != "Bill_Hat" {
		t.Fatalf("removal = %+v %v", r, ok)
	}
}

// A bill the journal never placed (a player's, another load's, an unmigrated
// owner's whose Standard the review does not bind) is kept however long no
// planner declares it: orphan removal covers a migrated owner's bills only.
func TestLedgerKeepsABillNoMigratedOwnerPlaced(t *testing.T) {
	f := newLedgerFixture(t)
	f.declare(ledgerOrder("Make_Vest"))
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_Hat", ledgerOrder("Make_Hat")), fakeBill("Bill_Vest", ledgerOrder("Make_Vest")))}
	for round := 0; round < 3*policy.OrphanGraceRounds; round++ {
		if r := f.round(); r.Plan != "" {
			t.Fatalf("round %d committed %s", round, r.Plan)
		}
	}
}

// A bill the ledger itself placed (a migrated owner's, so removable in
// general) is never removed when its recipe is declare-only: the surgery part,
// medicine, baby food and mech gestation bills of #2604, however long the
// planner has declared nothing. It also satisfies its declared order, so the
// ledger does not place a second one.
func TestLedgerNeverRemovesADeclareOnlyBill(t *testing.T) {
	for _, kind := range []policy.LedgerBillKind{policy.LedgerSurgery, policy.LedgerMedical, policy.LedgerBabyFood, policy.LedgerMechGestation} {
		t.Run(string(kind), func(t *testing.T) {
			f := newLedgerFixture(t)
			hat := ledgerOrder("Make_Hat")
			f.declare(hat)
			f.native.benches = []policy.GearBench{ledgerBenchRow()}
			f.place(f.round().Plan, "Bill_Hat")
			standing := fakeBill("Bill_Hat", hat)
			standing.Kind = kind
			f.native.benches = []policy.GearBench{ledgerBenchRow(standing)}
			if r := f.round(); r.Plan != "" {
				t.Fatalf("a declared bill of kind %s was placed again: %s", kind, r.Plan)
			}
			f.declare()
			for round := 0; round < 3*policy.OrphanGraceRounds; round++ {
				if r := f.round(); r.Plan != "" {
					t.Fatalf("round %d removed a %s bill: %s", round, kind, r.Plan)
				}
			}
		})
	}
}

// A planner that abstained stops orphan removal for the Round.
func TestLedgerAbstainKeepsOrphans(t *testing.T) {
	f := newLedgerFixture(t)
	f.declarer.declared = policy.Declared{Abstain: true}
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_Hat", ledgerOrder("Make_Hat")))}
	for round := 0; round < 2*policy.OrphanGraceRounds; round++ {
		if r := f.round(); r.Plan != "" {
			t.Fatalf("round %d removed under an abstain: %s", round, r.Plan)
		}
	}
}

// With no declarer the ledger is inactive and never touches a bill, and an
// unread bench readback makes the finding unknown rather than a diff.
func TestLedgerIdleWithoutDeclarersOrReadback(t *testing.T) {
	f := newLedgerFixture(t)
	f.native.benches = []policy.GearBench{ledgerBenchRow(fakeBill("Bill_Hat", ledgerOrder("Make_Hat")))}
	f.reviewer.ledger.declarers = nil
	for round := 0; round < 2*policy.OrphanGraceRounds; round++ {
		if r := f.round(); r.Plan != "" {
			t.Fatalf("an inactive ledger committed %s", r.Plan)
		}
	}
	f.reviewer.AddOrderDeclarer(f.declarer)
	f.declare(ledgerOrder("Make_Vest"))
	unread := ledgerBenchRow()
	unread.Bills = domain.Unknown[[]policy.GearBill]()
	f.native.benches = []policy.GearBench{unread}
	if r := f.round(); r.Plan != "" {
		t.Fatalf("committed on an unread census: %s", r.Plan)
	}
}

// The plan's content decides its claims and its method id: removals claim the
// bill, every action its bench, and the same content is a fresh attempt.
func TestLedgerActionsClaimBenchesAndBills(t *testing.T) {
	pending := &ledgerPending{
		remove: []policy.ActualBill{{ID: "Bill_9", Bench: "Bench_2"}},
		place:  []policy.LedgerPlacement{{Bench: ledgerBench, Spec: ledgerOrder("Make_Vest")}, {Bench: ledgerBench, Spec: ledgerOrder("Make_Hat")}},
	}
	actions, claims, key, err := ledgerActions("plan-1", pending)
	if err != nil || len(actions) != 3 {
		t.Fatalf("actions = %d %v", len(actions), err)
	}
	if _, removal := actions[0].RemoveProductionBill(); !removal {
		t.Fatal("removals come first")
	}
	if got := strings.Join(claims, ","); got != "bench:Bench_2,stale-bill:Bill_9,bench:"+ledgerBench {
		t.Fatalf("claims = %s", got)
	}
	ids := map[domain.ActionID]bool{}
	for _, a := range actions {
		ids[a.ID()] = true
	}
	if len(ids) != 3 || key == "" {
		t.Fatalf("ids = %v key = %q", ids, key)
	}
}
