package store

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoutineGoalRetirementSurvivesRepeatedReloadsAndRestart(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	first := reviewRoutine(t, s, &r)
	old := routineGoal(t, first, policy.MaintainWood)
	for i := 0; i < 32; i++ {
		r.Current.Load = domain.LoadID(fmt.Sprintf("load-%d", i))
		out := reviewRoutine(t, s, &r)
		if len(out.Goals) != 47 {
			t.Fatal(out)
		}
	}
	s.Close()
	s = open(t, path)
	g, err := s.LoadGoal(ctx, old.Goal.ID)
	if err != nil || !g.Retired || g.Goal.Status != domain.GoalInvalidated {
		t.Fatal(g, err)
	}
	var active, history int
	if err = s.db.QueryRowContext(ctx, "SELECT count(*),sum(retired=0) FROM goals").Scan(&history, &active); err != nil {
		t.Fatal(err)
	}
	if active != 47 || history != 47*33 {
		t.Fatal(active, history)
	}
	if _, err = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, r.Current, r.Tick, domain.NeedDeficit, false); err == nil {
		t.Fatal("retired goal reviewed")
	}
	if _, err = s.CancelGoal(ctx, g.Goal.ID, g.Revision); err == nil {
		t.Fatal("retired goal changed")
	}
	replacement, err := domain.NewGoal(old.Goal.ID, domain.AutopilotGoal, 2, r.Current, r.Tick)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreateGoal(ctx, replacement); err == nil {
		t.Fatal("retired identity reused")
	}
}

func TestRoutineGoalRetirementWaitsForObservedEffects(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainWood)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Prepare(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	r.Current.Load = "other"
	r.Current.Plan = "other"
	reviewRoutine(t, s, &r)
	before, err := s.LoadGoal(ctx, g.Goal.ID)
	if err != nil || before.Retired {
		t.Fatal(before, err)
	}
	if _, err = s.Observe(ctx, "p", domain.Observation{Action: "a", Attempt: 1, Snapshot: scope(), Tick: 11, Effect: domain.EffectCompleted}, scope()); err != nil {
		t.Fatal(err)
	}
	reviewRoutine(t, s, &r)
	after, err := s.LoadGoal(ctx, g.Goal.ID)
	if err != nil || !after.Retired || len(after.Methods) != 0 {
		t.Fatal(after, err)
	}
	if method, err := s.LoadGoalMethod(ctx, g.Goal.ID, g.Goal.Epoch, "wood"); err != nil || method.Plan != "p" {
		t.Fatal(method, err)
	}
	if methods, err := s.LoadGoalMethods(ctx, g.Goal.ID, g.Goal.Epoch); err != nil || len(methods) != 1 || methods[0].Plan != "p" {
		t.Fatal("retired method evidence missing", methods, err)
	}
	if _, err := s.LoadGoalMethods(ctx, g.Goal.ID, g.Goal.Epoch+1); err == nil {
		t.Fatal("future method epoch accepted")
	}
	p, err := s.LoadPlan(ctx, "p")
	if err != nil || p.Progress[0].View().Unresolved {
		t.Fatal(p, err)
	}
	if _, err = s.Prepare(ctx, "p", "a", scope(), 12); err == nil {
		t.Fatal("retired method prepared")
	}
}

func TestRoutineGoalRetirementRollsBackWithReview(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	if _, err := s.db.ExecContext(ctx, `CREATE TRIGGER fail_review BEFORE UPDATE ON routine_review BEGIN SELECT RAISE(ABORT,'review failure'); END`); err != nil {
		t.Fatal(err)
	}
	r.Current.Load = "other"
	if _, err := s.ReviewRoutine(ctx, r); err == nil {
		t.Fatal("injected failure ignored")
	}
	for _, old := range out.Goals {
		g, err := s.LoadGoal(ctx, old.Goal.ID)
		if err != nil || g.Retired || g.Goal != old.Goal {
			t.Fatal(g, err)
		}
	}
}

func TestRoutineGoalRetirementRetainsCompletedOwnedDraft(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainWood)
	draft, err := domain.NewOwnedDraft("pawn")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewOwnedDraftAction("draft", draft)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("p", scope().Revision, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "owned", p); err != nil {
		t.Fatal(err)
	}
	v := draftPlan{Plan: "p", Action: "draft"}
	observed := scope()
	observed.Native = 2
	a := DraftAdmission{Snapshot: observed, Tick: 10, Pawn: "pawn", PawnSnapshotToken: "cas"}
	claim := draftDispatch(t, s, v, a)
	ob := domain.Observation{Action: "draft", Attempt: 1, Snapshot: observed, Tick: 10, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}
	progress, err := s.ObserveDraft(ctx, "p", ob, observed, domain.Known(claim))
	if err != nil || progress.View().Stage != domain.Completed || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	r.Current.Load = "other"
	r.Current.Plan = "other"
	reviewRoutine(t, s, &r)
	g, err = s.LoadGoal(ctx, g.Goal.ID)
	if err != nil || g.Retired || g.Goal.Status != domain.GoalInvalidated {
		t.Fatal("owned draft cleanup lost", g, err)
	}
	retained, err := s.LoadPlan(ctx, "p")
	if err != nil || retained.Retired {
		t.Fatal("owned draft plan retired", retained, err)
	}
}
