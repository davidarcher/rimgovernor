package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// staged rebuilds r's plan with each action requiring the one before it,
// so only the first is in the next admitted segment.
func staged(t *testing.T, r BuildingMethodRequest) BuildingMethodRequest {
	t.Helper()
	actions := r.Plan.Actions()
	var deps []domain.ActionDependency
	for i := 1; i < len(actions); i++ {
		deps = append(deps, domain.ActionDependency{Action: actions[i].ID(), Requires: actions[i-1].ID()})
	}
	p, err := domain.NewPlan(r.Plan.ID(), r.Plan.Revision(), actions, deps...)
	if err != nil {
		t.Fatal(err)
	}
	r.Plan = p
	return r
}

func commitmentAmounts(rows []PlanCommitment) map[domain.ActionID]int64 {
	out := map[domain.ActionID]int64{}
	for _, c := range rows {
		out[c.Action] += c.Amount.Count
	}
	return out
}

// A long plan commits only its next admitted segment: the first action's
// cost is held, the dependency-blocked remainder is demand, and completing
// the first action moves the next one into the segment (#628). A held
// quantity that never dispatches expires past the horizon into demand.
func TestPlanCommitmentsHoldNextSegmentAndExpire(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	r := staged(t, methodRequest(t, g, "long", 60, 30))
	if d, err := s.AdmitBuildingMethod(ctx, r); err != nil || !d.Admitted {
		t.Fatal(d, err)
	}
	first, second := r.Plan.Actions()[0].ID(), r.Plan.Actions()[1].ID()
	view, err := s.LoadPlanCommitments(ctx, scope(), 10, 100)
	if err != nil {
		t.Fatal(err)
	}
	if held, demand := commitmentAmounts(view.Committed), commitmentAmounts(view.Demand); len(view.Committed) != 1 || held[first] != 60 || len(view.Demand) != 1 || demand[second] != 30 || view.Demand[0].Release != CommitmentBlocked {
		t.Fatalf("committed %+v demand %+v", view.Committed, view.Demand)
	}
	c := view.Committed[0]
	if c.Plan != r.Plan.ID() || c.Goal != g.Goal.ID || c.Urgency != g.Goal.Priority || c.Since != 10 || c.Expires != 110 || c.Release != CommitmentHeld || !c.Preemptible || c.Amount.Resource != "WoodLog" {
		t.Fatalf("%+v", c)
	}
	if totals := view.CommittedTotals(); totals["WoodLog"] != 60 {
		t.Fatal(totals)
	}
	// Past the horizon the undispatched hold is released as demand.
	if view, err = s.LoadPlanCommitments(ctx, scope(), 200, 100); err != nil || len(view.Committed) != 0 || len(view.Demand) != 2 || view.Demand[0].Release != CommitmentExpired {
		t.Fatalf("%+v %v", view, err)
	}
	// Dispatching the first action refreshes its evidence tick and a
	// dispatched order never expires; completing it releases its cost and
	// admits the second into the segment.
	initial, err := s.LoadPlan(ctx, r.Plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReserveAndPrepare(ctx, r.Plan.ID(), first, initial.Admissions[0].Admission); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, r.Plan.ID(), first, r.Current, 10); err != nil {
		t.Fatal(err)
	}
	if view, err = s.LoadPlanCommitments(ctx, scope(), 200, 100); err != nil || len(view.Committed) != 1 || view.Committed[0].Action != first || view.Committed[0].Preemptible {
		t.Fatalf("%+v %v", view, err)
	}
	if _, err = s.Observe(ctx, r.Plan.ID(), domain.Observation{Action: first, Attempt: 1, Snapshot: r.Current, Tick: 11, Effect: domain.EffectCompleted}, r.Current); err != nil {
		t.Fatal(err)
	}
	if view, err = s.LoadPlanCommitments(ctx, scope(), 20, 100); err != nil || len(view.Committed) != 1 || view.Committed[0].Action != second || view.Committed[0].Amount.Count != 30 || len(view.Demand) != 0 {
		t.Fatalf("%+v %v", view, err)
	}
	// A plan admitted under another load holds nothing here.
	other := scope()
	other.Load = "reloaded"
	if view, err = s.LoadPlanCommitments(ctx, other, 20, 100); err != nil || len(view.Committed) != 0 || len(view.Demand) != 0 {
		t.Fatalf("%+v %v", view, err)
	}
}

// Preemption retires the lower plan through the ordinary cancellation rows:
// its actions are cancelled, its goal stays active at a new revision, the
// commitments view no longer holds its quantity and a competing method can
// spend it at once. A stale revision or dispatched work refuses.
func TestPreemptGoalMethodRetiresUndispatchedPlan(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	r := methodRequest(t, g, "shelter", 90)
	d, err := s.AdmitBuildingMethod(ctx, r)
	if err != nil || !d.Admitted {
		t.Fatal(d, err)
	}
	other := anotherGoal(t, s, "medical")
	q := methodRequest(t, other, "bed", 40)
	if d, err = s.AdmitBuildingMethod(ctx, q); err != nil || d.Admitted {
		t.Fatal("stock held by the shelter admitted the bed", d, err)
	}
	if _, err = s.PreemptGoalMethod(ctx, g.Goal.ID, d.Goal.Revision+7, r.Plan.ID()); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = s.PreemptGoalMethod(ctx, g.Goal.ID, g.Revision+1, "unknown"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	view, err := s.LoadPlanCommitments(ctx, scope(), 10, 100)
	if err != nil || len(view.Committed) != 1 {
		t.Fatal(view, err)
	}
	state, err := s.PreemptGoalMethod(ctx, view.Committed[0].Goal, view.Committed[0].Revision, view.Committed[0].Plan)
	if err != nil || state.Revision != view.Committed[0].Revision+1 || state.Goal.Status != domain.GoalActive {
		t.Fatalf("%+v %v", state, err)
	}
	p, err := s.LoadPlan(ctx, r.Plan.ID())
	if err != nil || p.Progress[0].View().Stage != domain.Cancelled || p.Progress[0].View().Unresolved {
		t.Fatalf("%+v %v", p, err)
	}
	if view, err = s.LoadPlanCommitments(ctx, scope(), 10, 100); err != nil || len(view.Committed) != 0 || len(view.Demand) != 0 {
		t.Fatalf("%+v %v", view, err)
	}
	if d, err = s.AdmitBuildingMethod(ctx, q); err != nil || !d.Admitted {
		t.Fatal("released quantity not claimable", d, err)
	}
	// Dispatched work is never retired from software.
	bed := q.Plan.Actions()[0].ID()
	admitted, err := s.LoadPlan(ctx, q.Plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReserveAndPrepare(ctx, q.Plan.ID(), bed, admitted.Admissions[0].Admission); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, q.Plan.ID(), bed, q.Current, 10); err != nil {
		t.Fatal(err)
	}
	if view, err = s.LoadPlanCommitments(ctx, scope(), 10, 100); err != nil || len(view.Committed) != 1 || view.Committed[0].Preemptible {
		t.Fatalf("%+v %v", view, err)
	}
	if _, err = s.PreemptGoalMethod(ctx, view.Committed[0].Goal, view.Committed[0].Revision, view.Committed[0].Plan); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = s.LoadPlanCommitments(ctx, scope(), -1, 100); err == nil {
		t.Fatal("negative tick accepted")
	}
}
