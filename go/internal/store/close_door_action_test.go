package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A close_door (#1743) persists its cell.
func TestCloseDoorActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	want, err := domain.NewCloseDoor(domain.Cell{X: 12, Z: 31})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewCloseDoorAction("close1", want)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("close-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "close-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].CloseDoor(); !ok || v != want {
		t.Fatal(v, want)
	}
}
