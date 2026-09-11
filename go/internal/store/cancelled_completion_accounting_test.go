package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCancelledMethodCompletionReleasesFreshBudgetAfterRestart(t *testing.T) {
	ctx := context.Background()
	s, path, g := goalFixture(t)
	r := methodRequest(t, g, "old", 100)
	d, err := s.AdmitBuildingMethod(ctx, r)
	if err != nil || !d.Admitted {
		t.Fatal(d, err)
	}
	action := r.Plan.Actions()[0].ID()
	initial, err := s.LoadPlan(ctx, r.Plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReserveAndPrepare(ctx, r.Plan.ID(), action, initial.Admissions[0].Admission); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, r.Plan.ID(), action, r.Current, r.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.CancelGoal(ctx, g.Goal.ID, d.Goal.Revision); err != nil {
		t.Fatal(err)
	}
	other := anotherGoal(t, s, "other")
	q := methodRequest(t, other, "new", 100)
	q.Tick = 12
	q.Stock.Tick = 12
	q.Previews[0].Tick = 12
	if d, err = s.AdmitBuildingMethod(ctx, q); err != nil || d.Admitted {
		t.Fatal("unknown cancellation released", d, err)
	}
	if _, err = s.Observe(ctx, r.Plan.ID(), domain.Observation{Action: action, Attempt: 1, Snapshot: r.Current, Tick: 11, Effect: domain.EffectCompleted}, r.Current); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	q.Stock.Tick = 10
	if d, err = s.AdmitBuildingMethod(ctx, q); err != nil || d.Admitted {
		t.Fatal("stale stock released", d, err)
	}
	q.Stock.Tick = 12
	q.Previews[0].SafeToPlace = domain.Known(false)
	if d, err = s.AdmitBuildingMethod(ctx, q); err != nil || d.Admitted {
		t.Fatal("unsafe placement admitted", d, err)
	}
	q.Previews[0].SafeToPlace = domain.Known(true)
	if d, err = s.AdmitBuildingMethod(ctx, q); err != nil || !d.Admitted {
		t.Fatal("fresh replacement blocked", d, err)
	}
	old, err := s.LoadPlan(ctx, r.Plan.ID())
	if err != nil || old.Progress[0].View().Stage != domain.Cancelled || old.Progress[0].View().Unresolved || old.Admissions[0].Admission.Costs[0].Count != 100 {
		t.Fatal("history changed", old, err)
	}
}
