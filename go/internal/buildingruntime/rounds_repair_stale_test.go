package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func staleRepairGoal(t *testing.T, journal *store.Store) store.StandardState {
	t.Helper()
	ctx := context.Background()
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p", Revision: domain.PlanRevision(^uint64(0))}
	g, err := domain.NewStandard("repairs", 3, snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.SeedStandard(ctx, g); err != nil {
		t.Fatal(err)
	}
	state, err := journal.ReviewStandard(ctx, g.ID, 0, snapshot, 10, domain.FindingUnmet)
	if err != nil {
		t.Fatal(err)
	}
	repair, err := domain.NewRepair("Thing_Human1", "Thing_Wall1", domain.Cell{X: 1, Z: 1})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRepairAction("repair-plan-0", repair)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("repair-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if state, err = journal.CommitMethod(ctx, g.ID, state.Revision, "repair-0", plan); err != nil {
		t.Fatal(err)
	}
	return state
}

// Once the goal itself recovers, every pending repair is moot whatever its
// hold says: nothing was issued and no deficit remains.
func TestCancelSettledRepairMethodsCancelsPendingWorkOfARecoveredGoal(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, filepath.Join(t.TempDir(), "recovered.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	goal := staleRepairGoal(t, journal)
	if _, err = journal.Hold(ctx, "repair-plan", "repair-plan-0", []domain.HeldReason{domain.HeldStaleFacts}, 100); err != nil {
		t.Fatal(err)
	}
	snapshot := goal.Standard.Snapshot
	if goal, err = journal.ReviewStandard(ctx, goal.Standard.ID, goal.Revision, snapshot, 200, domain.FindingMet); err != nil {
		t.Fatal(err)
	}
	if goal.Standard.Finding != domain.FindingMet || goal.Standard.Status != domain.StandardOpen {
		t.Fatalf("recovered goal with open work: need=%s status=%s", goal.Standard.Finding, goal.Standard.Status)
	}
	if err = cancelSettledRepairMethods(ctx, journal, goal); err != nil {
		t.Fatal(err)
	}
	plan, err := journal.LoadPlan(ctx, "repair-plan")
	if err != nil {
		t.Fatal(err)
	}
	if v := plan.Progress[0].View(); v.Stage != domain.Cancelled || store.PlanOpen(plan) {
		t.Fatalf("stage = %s, want cancelled with no open work", v.Stage)
	}
}
