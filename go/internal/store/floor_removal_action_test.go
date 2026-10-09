package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A floor removal persists its floor def and cell.
func TestFloorRemovalActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewFloorRemoval("WoodPlankFloor", domain.Cell{X: 12, Z: 5})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewFloorRemovalAction("floor", value)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("floor-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "floor-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].FloorRemoval(); !ok || v != value {
		t.Fatal(v, ok)
	}
}
