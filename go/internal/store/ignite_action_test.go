package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An ignite (#1815) persists its pawn and cell.
func TestIgniteActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	want, err := domain.NewIgnite("Human12", domain.Cell{X: 12, Z: 31})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewIgniteAction("ignite1", want)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("ignite-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "ignite-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].Ignite(); !ok || v != want {
		t.Fatal(v, want)
	}
}
