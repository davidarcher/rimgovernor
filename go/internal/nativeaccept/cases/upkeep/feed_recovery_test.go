package upkeep

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The retained #558 journal has an accepted bill at 77167/generation 16,
// followed by OutcomeNotAchieved at 79894/generation 17. The native receipt
// reports a changed configuration and 35 observed Kibble; the plan stays failed.
func TestFeedBillRecoveryFromJournal(t *testing.T) {
	ctx := context.Background()
	journal, err := store.Open(ctx, filepath.Join(t.TempDir(), "journal.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	bill, err := domain.NewProductionBill("Thing_ButcherSpot45302", "Make_Kibble", "bill-before", domain.StockTarget, 65)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("routine-resource-a6c9c5bce4497aad60210a4acfbb6bed-0", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("routine-resource-a6c9c5bce4497aad60210a4acfbb6bed", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: plan.ID(), Revision: 1, Native: 16}
	if _, err = journal.PrepareBill(ctx, plan.ID(), action.ID(), store.BillAdmission{Snapshot: snapshot, Tick: 76244, Bench: "Thing_ButcherSpot45302", SnapshotToken: "bill-before"}); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.Dispatch(ctx, plan.ID(), action.ID(), snapshot, 76680); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.RecordReceipt(ctx, plan.ID(), action.ID(), 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	snapshot.Native = 17
	if _, err = journal.Observe(ctx, plan.ID(), domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: 79894, Effect: domain.EffectUnsuccessful, Causality: domain.AfterDispatch, UnsuccessfulReason: domain.OutcomeNotAchieved}, snapshot); err != nil {
		t.Fatal(err)
	}
	assertFeedBillRecovery(t, journal, plan.ID())
}

func assertFeedBillRecovery(t *testing.T, journal *store.Store, id domain.PlanID) {
	t.Helper()
	state, _, _, err := waitPlanOrRecovery(context.Background(), journal, id, func() (bool, error) { return false, nil })
	if err == nil {
		t.Fatal("changed order must remain unsuccessful")
	}
	if !feedBillNeedsRecovery(policy.MaintainAnimalFeed, state) {
		t.Fatal("feed must continue to its separate recovery and native-stock checks")
	}
	if feedBillNeedsRecovery(policy.MaintainMedicalReserves, state) {
		t.Fatal("unrelated failure accepted")
	}
	if state.Progress[0].View().Stage != domain.Unsuccessful {
		t.Fatal("recovery handoff changed plan status")
	}
	state.Progress = nil
	if feedBillNeedsRecovery(policy.MaintainAnimalFeed, state) {
		t.Fatal("missing evidence accepted")
	}
}
