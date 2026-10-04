package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Plan ids are minted (#985): the double-admission guard is the
// (goal, epoch, method) key, and planners whose work outlives an epoch
// find its plan by (goal, method), newest epoch first.
func TestMethodPlanKey(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	first := domain.MintPlanID()
	g, e := s.CommitGoalMethod(ctx, g.Standard.ID, g.Revision, "shell", plan(t, first, domain.ActionID(first+"-0")))
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.Cancel(ctx, first, domain.ActionID(first+"-0")); e != nil {
		t.Fatal(e)
	}
	again := domain.MintPlanID()
	if _, e = s.CommitGoalMethod(ctx, g.Standard.ID, g.Revision, "shell", plan(t, again, domain.ActionID(again+"-0"))); !errors.Is(e, ErrConflict) {
		t.Fatal("same key admitted twice under a fresh plan id", e)
	}
	// Later epochs of the same method, as a re-opened goal writes them.
	var latest domain.PlanID
	for _, epoch := range []string{"9", "10"} {
		id := domain.MintPlanID()
		if e = s.CreatePlan(ctx, plan(t, id, domain.ActionID(id+"-0"))); e != nil {
			t.Fatal(e)
		}
		if _, e = s.db.ExecContext(ctx, "INSERT INTO goal_methods(goal_id,epoch,method_id,plan_id,priority) VALUES(?,?,?,?,?)", g.Standard.ID, epoch, "shell", id, 1); e != nil {
			t.Fatal(e)
		}
		latest = id
	}
	for _, c := range []struct {
		goal   domain.ConcernID
		method domain.MethodID
		want   domain.PlanID
		err    error
	}{
		{g.Standard.ID, "shell", latest, nil},
		{g.Standard.ID, "other", "", ErrNotFound},
		{"missing", "shell", "", ErrNotFound},
	} {
		got, e := s.LatestMethodPlan(ctx, c.goal, c.method)
		if got != c.want || !errors.Is(e, c.err) {
			t.Errorf("%s/%s: got %q %v, want %q %v", c.goal, c.method, got, e, c.want, c.err)
		}
	}
}
