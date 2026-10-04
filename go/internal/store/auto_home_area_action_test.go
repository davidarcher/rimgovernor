package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An auto home area action (#1322) persists the value it sets.
func TestAutoHomeAreaActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	off, err := domain.NewAutoHomeAreaAction("off", false)
	if err != nil {
		t.Fatal(err)
	}
	on, err := domain.NewAutoHomeAreaAction("on", true)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("auto-home-plan", 1, []domain.Action{off, on})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "auto-home-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 2 {
		t.Fatal(got)
	}
	for i, want := range []bool{false, true} {
		v, ok := got[i].AutoHomeArea()
		if !ok || v != want {
			t.Fatal(i, v, ok)
		}
	}
}
