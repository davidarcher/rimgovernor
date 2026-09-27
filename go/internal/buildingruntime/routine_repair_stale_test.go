package buildingruntime

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func staleRepairGoal(t *testing.T, journal *store.Store) store.GoalState {
	t.Helper()
	ctx := context.Background()
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p", Revision: domain.PlanRevision(^uint64(0))}
	g, err := domain.NewGoal("repairs", domain.AutopilotGoal, 3, snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreateGoal(ctx, g); err != nil {
		t.Fatal(err)
	}
	state, err := journal.ReviewGoal(ctx, g.ID, 0, snapshot, 10, domain.NeedDeficit, false)
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
	if state, err = journal.CommitGoalMethod(ctx, g.ID, state.Revision, "repair-0", plan); err != nil {
		t.Fatal(err)
	}
	return state
}

// Once the goal itself recovers, every pending repair is moot whatever its
// hold says: nothing was issued and no deficit remains.
func TestCancelSettledRepairMethodsCancelsPendingWorkOfARecoveredGoal(t *testing.T) {
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
	snapshot := goal.Goal.Snapshot
	if goal, err = journal.ReviewGoal(ctx, goal.Goal.ID, goal.Revision, snapshot, 200, domain.NeedRecovered, false); err != nil {
		t.Fatal(err)
	}
	if goal.Goal.Need != domain.NeedRecovered || goal.Goal.Status != domain.GoalActive {
		t.Fatalf("recovered goal with open work: need=%s status=%s", goal.Goal.Need, goal.Goal.Status)
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
