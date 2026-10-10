package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A deconstruction persists its door-to-wall swap; one without keeps none.
func TestDeconstructionSwapRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	base, err := domain.NewDeconstruction("Thing_Wall1", "Wall", domain.Cell{X: 10, Z: 12})
	if err != nil {
		t.Fatal(err)
	}
	a0, err := domain.NewDeconstructionAction("plain", base)
	if err != nil {
		t.Fatal(err)
	}
	swap := base.WithWallReplacement()
	a1, err := domain.NewDeconstructionAction("swap", swap)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("deconstruct-plan", 1, []domain.Action{a0, a1})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "deconstruct-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 2 {
		t.Fatal(got)
	}
	v0, _ := got[0].Deconstruction()
	v1, _ := got[1].Deconstruction()
	if v0 != base || v0.ReplacesWithWall() || v1 != swap || !v1.ReplacesWithWall() {
		t.Fatal(v0, v1)
	}
}
