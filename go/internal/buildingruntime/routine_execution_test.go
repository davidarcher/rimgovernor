package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

func TestRoutineWorkerPreservesPlayerPriorityAndCancellation(t *testing.T) {
	planner, db, base, _, _ := sleepingFixture(t)
	ctx := context.Background()
	method, err := planner.Step(ctx)
	if err != nil || !method.Decision.Admitted {
		t.Fatal(method, err)
	}
	p := planner.reviewer.player
	f := &workerFake{playerFakeSession: base}
	p.session = f
	w := &Worker{player: p, session: f, config: WorkerConfig{RoutineMethods: true, StepInterval: time.Millisecond, MaxBackoff: time.Second, StepTimeout: time.Second}, waits: make(map[domain.ActionID]workerWait)}
	root := base.State()
	var selected domain.PlanID
	f.run = func(_ context.Context, plan domain.PlanID, _ domain.ActionID) (executor.Result, error) {
		selected = plan
		return executor.Result{}, nil
	}
	if err = w.step(ctx, time.Now()); err != nil || selected != root.Snapshot.Plan {
		t.Fatal(selected, err)
	}
	playerPlan, err := db.LoadPlan(ctx, root.Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range playerPlan.Spec.Actions() {
		if _, err = db.Cancel(ctx, playerPlan.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	selected = ""
	if err = w.step(ctx, time.Now().Add(time.Second)); err != nil || selected != method.Decision.Goal.Methods[0].Plan {
		t.Fatal(selected, err)
	}
	if base.State() != root {
		t.Fatal("routine selection changed player authority")
	}
	if _, err = db.CancelGoal(ctx, method.Decision.Goal.Goal.ID, method.Decision.Goal.Revision); err != nil {
		t.Fatal(err)
	}
	selected = ""
	if err = w.step(ctx, time.Now().Add(2*time.Second)); err != nil || selected != "" {
		t.Fatal("cancelled routine ran", selected, err)
	}
}

func TestRoutineClockIncludesMethodsAfterPlayerPlanSettles(t *testing.T) {
	s, f := schedulerFixture(t)
	schedulerSleeping(t, s, f)
	s.session.routineMethods = true
	s.config.RoutineMethods = true
	root := s.session.State().Snapshot
	p, err := s.player.journal.LoadPlan(context.Background(), root.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range p.Spec.Actions() {
		if _, err = s.player.journal.Cancel(context.Background(), p.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	result, err := s.Step(context.Background())
	if err != nil || result.Sleeping == nil || result.Sleeping.Reason != BuildingMethodAdmitted || f.writes != 1 {
		t.Fatal(result, err, f.writes)
	}
}
