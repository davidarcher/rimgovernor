package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

// pendingHarvest journals one dispatched plant-harvest acquisition whose
// effect native keeps reporting pending (designated, nobody harvesting):
// dispatched at tick 100, observed pending at 200 and again at 40000, the
// way colony-6's wild healroot sat (#291).
func pendingHarvest(t *testing.T, db *store.Store, plan domain.PlanID, action domain.ActionID, thing string) domain.GenerationSnapshot {
	t.Helper()
	ctx := context.Background()
	value, err := domain.NewAcquisition(thing, "MedicineHerbal", domain.Cell{X: 140, Z: 110})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewAcquisitionAction(action, value)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan(plan, 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(ctx, spec); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: spec.ID(), Revision: spec.Revision(), Native: 1}
	if _, err = db.PrepareAcquisition(ctx, plan, action, store.AcquisitionAdmission{Snapshot: snapshot, Tick: 100, Thing: thing, SnapshotToken: "cas-1"}); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Dispatch(ctx, plan, action, snapshot, 100); err != nil {
		t.Fatal(err)
	}
	if _, err = db.RecordReceipt(ctx, plan, action, 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	for _, tick := range []domain.Tick{200, 40000} {
		if _, err = db.Observe(ctx, plan, domain.Observation{Action: action, Attempt: 1, Snapshot: snapshot, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}, snapshot); err != nil {
			t.Fatal(err)
		}
	}
	return snapshot
}

func TestStalledAcquisitionDesignationsMeasureFromDispatch(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := storetest.Path(t)
	db, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = db.Close() }()
	pendingHarvest(t, db, "medical-plan", "medical-plan-0", "Thing_Plant_HealrootWild16865")
	plan, err := db.LoadPlan(ctx, "medical-plan")
	if err != nil {
		t.Fatal(err)
	}
	// The pending observations moved the view's tick to 40000; the stall
	// bound counts from the dispatch at 100, not the latest observation.
	if got, err := stalledAcquisitionDesignations(ctx, db, plan.Progress, nil, 100+59999, harvestContract(60000)); err != nil || got != nil {
		t.Fatal("stalled before the bound elapses", got, err)
	}
	got, err := stalledAcquisitionDesignations(ctx, db, plan.Progress, nil, 100+60000, harvestContract(60000))
	if err != nil || len(got) != 1 || got[0].Action != "medical-plan-0" || got[0].Thing != "Thing_Plant_HealrootWild16865" {
		t.Fatal("did not report the stalled designation", got, err)
	}
	if got, err := stalledAcquisitionDesignations(ctx, db, plan.Progress, nil, 100+60000, harvestContract(0)); err != nil || got != nil {
		t.Fatal("zero bound must disable stall detection", got, err)
	}
	// Hunts have their own bound (stalledHuntActions) and are not designations.
	hunts := map[string]bool{"Thing_Plant_HealrootWild16865": true}
	if got, err := stalledAcquisitionDesignations(ctx, db, plan.Progress, hunts, 100+60000, harvestContract(60000)); err != nil || got != nil {
		t.Fatal("hunt sources are not stalled designations", got, err)
	}
	// Cancelling it stops the planner from re-reporting it, but the method's
	// work stays open until the executor withdraws the designation natively
	// and observes the withdrawn record's terminal effect; a reopened store
	// replays that withdrawal transition.
	if _, err = db.Cancel(ctx, "medical-plan", got[0].Action); err != nil {
		t.Fatal(err)
	}
	if plan, err = db.LoadPlan(ctx, "medical-plan"); err != nil || !domain.GoalWorkOpen(plan.Progress) {
		t.Fatal("cancelled designation released the work before its withdrawal", err)
	}
	if got, err = stalledAcquisitionDesignations(ctx, db, plan.Progress, nil, 100+60000, harvestContract(60000)); err != nil || got != nil {
		t.Fatal("cancelled designation reported as stalled again", got, err)
	}
	snapshot := plan.Progress[0].View().Snapshot
	if _, err = db.Withdraw(ctx, "medical-plan", "medical-plan-0", snapshot, 40100); err != nil {
		t.Fatal(err)
	}
	if _, err = db.RecordReceipt(ctx, "medical-plan", "medical-plan-0", 2, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Observe(ctx, "medical-plan", domain.Observation{Action: "medical-plan-0", Attempt: 2, Snapshot: snapshot, Tick: 40200, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.OutcomeNotAchieved, Causality: domain.AfterDispatch}, snapshot); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	if db, err = store.Open(ctx, path); err != nil {
		t.Fatal(err)
	}
	if plan, err = db.LoadPlan(ctx, "medical-plan"); err != nil || domain.GoalWorkOpen(plan.Progress) || plan.Progress[0].View().Attempt != 2 {
		t.Fatal("withdrawn designation still open after replay", err)
	}
}

func TestStalledAcquisitionDesignationsIgnoreResolvedAndUndispatchedWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	snapshot := pendingHarvest(t, db, "medical-plan", "medical-plan-0", "Thing_Plant_HealrootWild1")
	if _, err = db.Observe(ctx, "medical-plan", domain.Observation{Action: "medical-plan-0", Attempt: 1, Snapshot: snapshot, Tick: 50000, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot); err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(ctx, "medical-plan")
	if err != nil {
		t.Fatal(err)
	}
	if got, err := stalledAcquisitionDesignations(ctx, db, plan.Progress, nil, 100+60000, harvestContract(60000)); err != nil || got != nil {
		t.Fatal("a completed harvest is not stalled", got, err)
	}
	value, err := domain.NewAcquisition("Thing_Plant_HealrootWild2", "MedicineHerbal", domain.Cell{X: 1, Z: 1})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewAcquisitionAction("waiting-plan-0", value)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("waiting-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(ctx, spec); err != nil {
		t.Fatal(err)
	}
	if plan, err = db.LoadPlan(ctx, "waiting-plan"); err != nil {
		t.Fatal(err)
	}
	if got, err := stalledAcquisitionDesignations(ctx, db, plan.Progress, nil, 1000000, harvestContract(60000)); err != nil || got != nil {
		t.Fatal("an undispatched harvest is not stalled", got, err)
	}
}
