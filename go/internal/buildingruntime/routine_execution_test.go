package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

// One author: the player's submitted guidance plan and the routine method
// are both dispatched under the root plan's authority, without priority
// arbitration between them; cancellation removes each from dispatch.
func TestRoutineWorkerDispatchesGuidanceAndMethodsUnderRoot(t *testing.T) {
	t.Parallel()
	planner, db, base, _, _ := sleepingFixture(t)
	ctx := context.Background()
	method, err := planner.Step(ctx)
	if err != nil || !method.Decision.Admitted {
		t.Fatal(method, err)
	}
	p := planner.reviewer.player
	f := &workerFake{playerFakeSession: base}
	p.session = f
	w := &Worker{player: p, session: f, config: WorkerConfig{RoutineMethods: true, StepInterval: time.Millisecond, MaxBackoff: time.Second, StepTimeout: 5 * time.Second}, waits: make(map[domain.ActionID]workerWait)}
	root := base.State()
	guidance := playerPlan(t, db)
	selected := map[domain.PlanID]bool{}
	f.run = func(_ context.Context, plan domain.PlanID, _ domain.ActionID) (executor.Result, error) {
		selected[plan] = true
		return executor.Result{}, nil
	}
	for i := 0; i < 4; i++ {
		if err = w.step(ctx, time.Now().Add(time.Duration(i)*time.Second)); err != nil {
			t.Fatal(err)
		}
	}
	if !selected[guidance.Spec.ID()] || !selected[method.Decision.Goal.Methods[0].Plan] || selected[root.Snapshot.Plan] {
		t.Fatal(selected)
	}
	if base.State() != root {
		t.Fatal("dispatch changed root authority")
	}
	for _, action := range guidance.Spec.Actions() {
		if _, err = db.Cancel(ctx, guidance.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	if _, err = db.CancelGoal(ctx, method.Decision.Goal.Goal.ID, method.Decision.Goal.Revision); err != nil {
		t.Fatal(err)
	}
	selected = map[domain.PlanID]bool{}
	if err = w.step(ctx, time.Now().Add(10*time.Second)); err != nil || len(selected) != 0 {
		t.Fatal("cancelled work ran", selected, err)
	}
}

func TestRoutineClockIncludesMethodsAfterPlayerPlanSettles(t *testing.T) {
	t.Parallel()
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
