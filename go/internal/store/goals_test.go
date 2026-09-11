package store

import (
	"context"
	"errors"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func goalFixture(t *testing.T) (*Store, string, GoalState) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "goals.db")
	s := open(t, path)
	g, e := domain.NewGoal("shelter", domain.AutopilotGoal, 2, scope(), 10)
	if e != nil {
		t.Fatal(e)
	}
	if e = s.CreateGoal(ctx, g); e != nil {
		t.Fatal(e)
	}
	state, e := s.ReviewGoal(ctx, g.ID, 0, scope(), 10, domain.NeedDeficit, false)
	if e != nil {
		t.Fatal(e)
	}
	return s, path, state
}
func TestGoalMethodAtomicCommitReopenAndDuplicate(t *testing.T) {
	ctx := context.Background()
	s, path, g := goalFixture(t)
	p := plan(t, "method-plan", "method-action")
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", p)
	if e != nil {
		t.Fatal(e)
	}
	s.Close()
	s = open(t, path)
	loaded, e := s.LoadGoal(ctx, g.Goal.ID)
	if e != nil || !reflect.DeepEqual(g, loaded) {
		t.Fatal(loaded, e)
	}
	if _, e = s.LoadPlan(ctx, p.ID()); e != nil {
		t.Fatal(e)
	}
	if _, e = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "orphan", "orphan-action")); e == nil {
		t.Fatal("unresolved method duplicated")
	}
	if _, e = s.LoadPlan(ctx, "orphan"); !errors.Is(e, ErrNotFound) {
		t.Fatal("failed method left a plan", e)
	}
	if _, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision-1, scope(), 11, domain.NeedRecovered, false); !errors.Is(e, ErrConflict) {
		t.Fatal("stale review accepted", e)
	}
}
func TestGoalCancellationRetainsIssuedUncertainty(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	p := plan(t, "p", "issued", "waiting")
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", p)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.Prepare(ctx, "p", "issued", scope(), 10); e != nil {
		t.Fatal(e)
	}
	if _, e = s.Dispatch(ctx, "p", "issued", scope(), 10); e != nil {
		t.Fatal(e)
	}
	g, e = s.CancelGoal(ctx, g.Goal.ID, g.Revision)
	if e != nil || g.Goal.Status != domain.GoalCancelled {
		t.Fatal(g, e)
	}
	state, e := s.LoadPlan(ctx, "p")
	if e != nil {
		t.Fatal(e)
	}
	if state.Progress[0].View().Stage != domain.Cancelled || !state.Progress[0].View().Unresolved || state.Progress[1].View().Stage != domain.Cancelled {
		t.Fatal(state.Progress)
	}
	if _, e = s.Prepare(ctx, "p", "waiting", scope(), 11); e == nil {
		t.Fatal("cancelled goal prepared")
	}
	if _, e = s.Observe(ctx, "p", domain.Observation{Action: "issued", Attempt: 1, Snapshot: scope(), Tick: 11, Effect: domain.EffectCompleted}, scope()); e != nil {
		t.Fatal("cancelled work could not reconcile", e)
	}
}
func TestGoalSuspensionGuardsPreparedDispatchAndCanResume(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "p", "a"))
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.Prepare(ctx, "p", "a", scope(), 10); e != nil {
		t.Fatal(e)
	}
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 11, domain.NeedDeficit, true)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.Dispatch(ctx, "p", "a", scope(), 11); e == nil {
		t.Fatal("suspended goal dispatched")
	}
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 12, domain.NeedDeficit, false)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.Dispatch(ctx, "p", "a", scope(), 12); e != nil {
		t.Fatal(e)
	}
}
func TestGoalObservedRecoveryThenRenewalKeepsOldPlan(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "p", "a"))
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.Prepare(ctx, "p", "a", scope(), 10); e != nil {
		t.Fatal(e)
	}
	if _, e = s.Dispatch(ctx, "p", "a", scope(), 10); e != nil {
		t.Fatal(e)
	}
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 11, domain.NeedRecovered, false)
	if e != nil || g.Goal.Status == domain.GoalSatisfied {
		t.Fatal(g, e)
	}
	if _, e = s.Observe(ctx, "p", domain.Observation{Action: "a", Attempt: 1, Snapshot: scope(), Tick: 11, Effect: domain.EffectCompleted}, scope()); e != nil {
		t.Fatal(e)
	}
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 12, domain.NeedRecovered, false)
	if e != nil || g.Goal.Status != domain.GoalSatisfied {
		t.Fatal(g, e)
	}
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 13, domain.NeedUnknown, false)
	if e != nil {
		t.Fatal(e)
	}
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 14, domain.NeedDeficit, false)
	if e != nil || g.Goal.Epoch != 1 {
		t.Fatal(g, e)
	}
	g, e = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "renewed", "new-action"))
	if e != nil || len(g.Methods) != 2 {
		t.Fatal(g, e)
	}
	old, e := s.LoadPlan(ctx, "p")
	if e != nil || old.Progress[0].View().Stage != domain.Completed {
		t.Fatal(old, e)
	}
}
func TestGoalDirectionInvalidationCancelsPendingPlan(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "p", "a"))
	if e != nil {
		t.Fatal(e)
	}
	changed := scope()
	changed.Direction--
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, changed, 11, domain.NeedDeficit, false)
	if e != nil || g.Goal.Status != domain.GoalInvalidated {
		t.Fatal(g, e)
	}
	p, e := s.LoadPlan(ctx, "p")
	if e != nil || p.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal(p, e)
	}
}

func TestGoalDuplicateMethodRollsBackPlanAfterTerminalWork(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "p", "a"))
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.Cancel(ctx, "p", "a"); e != nil {
		t.Fatal(e)
	}
	if _, e = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "orphan", "b")); !errors.Is(e, ErrConflict) {
		t.Fatal(e)
	}
	if _, e = s.LoadPlan(ctx, "orphan"); !errors.Is(e, ErrNotFound) {
		t.Fatal(e)
	}
	after, e := s.LoadGoal(ctx, g.Goal.ID)
	if e != nil || !reflect.DeepEqual(after, g) {
		t.Fatal(after, e)
	}
}

func TestGoalCancellationRollbackDoesNotPartiallyCancelActions(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "p", "a", "b"))
	if e != nil {
		t.Fatal(e)
	}
	_, e = s.db.ExecContext(ctx, `CREATE TRIGGER fail_goal_cancel BEFORE INSERT ON transitions WHEN NEW.action_id='b' BEGIN SELECT RAISE(ABORT,'test cancellation failure'); END`)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.CancelGoal(ctx, g.Goal.ID, g.Revision); e == nil {
		t.Fatal("expected injected failure")
	}
	after, e := s.LoadGoal(ctx, g.Goal.ID)
	if e != nil || !reflect.DeepEqual(after, g) {
		t.Fatal(after, e)
	}
	p, e := s.LoadPlan(ctx, "p")
	if e != nil {
		t.Fatal(e)
	}
	for _, progress := range p.Progress {
		if progress.View().Stage != domain.Pending {
			t.Fatal("partial cancellation", progress)
		}
	}
}

func TestGoalCorruptPayloadNeverAdmitsMethods(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	if _, e := s.db.ExecContext(ctx, "UPDATE goals SET payload=? WHERE id=?", []byte(`{"ID":"shelter"}`), g.Goal.ID); e != nil {
		t.Fatal(e)
	}
	if _, e := s.LoadGoal(ctx, g.Goal.ID); e == nil {
		t.Fatal("corrupt goal accepted")
	}
	if _, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "p", "a")); e == nil {
		t.Fatal("corrupt goal admitted method")
	}
}

func TestGoalUnknownReviewGuardsEveryPreparationPath(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "p", "a"))
	if e != nil {
		t.Fatal(e)
	}
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 11, domain.NeedUnknown, false)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.Prepare(ctx, "p", "a", scope(), 11); e == nil {
		t.Fatal("unknown need prepared")
	}
	if _, e = s.ReserveAndPrepare(ctx, "p", "a", Admission{Snapshot: scope(), Tick: 11, Costs: []MaterialCost{}, Footprint: []domain.Cell{{X: 3, Z: 7}}}); e == nil {
		t.Fatal("unknown need reserved")
	}
	p, e := s.LoadPlan(ctx, "p")
	if e != nil || p.Progress[0].View().Stage != domain.Pending || len(p.Admissions) != 0 {
		t.Fatal(p, e)
	}
}

func TestGoalRecoveredNeedFinishesAcceptedMethodWithoutStartingAnother(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	g, e := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "shell", plan(t, "p", "a"))
	if e != nil {
		t.Fatal(e)
	}
	g, e = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 11, domain.NeedRecovered, false)
	if e != nil || g.Goal.Status == domain.GoalSatisfied {
		t.Fatal(g, e)
	}
	if _, e = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "more", plan(t, "extra", "b")); e == nil {
		t.Fatal("recovered need created new work")
	}
	if _, e = s.Prepare(ctx, "p", "a", scope(), 11); e != nil {
		t.Fatal("accepted method stranded", e)
	}
	if _, e = s.Dispatch(ctx, "p", "a", scope(), 11); e != nil {
		t.Fatal(e)
	}
}
