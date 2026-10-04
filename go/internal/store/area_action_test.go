package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An area action (#1321) persists its operation, bot key (absent for home)
// and cells, including a delete's empty cell list.
func TestAreaActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	var actions []domain.Action
	for i, want := range []struct {
		op    domain.AreaOperation
		key   string
		cells []domain.Cell
	}{
		{domain.AreaCreate, "safe", []domain.Cell{{X: 2, Z: 3}}},
		{domain.AreaSetCells, "", []domain.Cell{{X: 5, Z: 1}, {X: 4, Z: 1}}},
		{domain.AreaDelete, "safe", nil},
	} {
		value, err := domain.NewArea(want.op, want.key, want.cells)
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewAreaAction(domain.ActionID([]string{"c", "h", "d"}[i]), value)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	p, err := domain.NewPlan("area-plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "area-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 3 {
		t.Fatal(got)
	}
	for i, a := range got {
		v, ok := a.Area()
		want, _ := actions[i].Area()
		if !ok || v != want {
			t.Fatal(i, v, want)
		}
	}
}
