package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A deconstruction persists its cleared ground (#1366) and door-to-wall
// swap (#1245); one without keeps none.
func TestDeconstructionClearedGroundRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	base, err := domain.NewDeconstruction("Thing_Wall1", "Wall", domain.Cell{X: 10, Z: 12})
	if err != nil {
		t.Fatal(err)
	}
	cleared, err := base.WithClearedGround([]domain.GroundRect{{Origin: domain.Cell{X: 8, Z: 9}, Width: 6, Height: 5}, {Origin: domain.Cell{X: 1, Z: 2}, Width: 1, Height: 1}})
	if err != nil {
		t.Fatal(err)
	}
	a0, err := domain.NewDeconstructionAction("plain", base)
	if err != nil {
		t.Fatal(err)
	}
	a1, err := domain.NewDeconstructionAction("ground", cleared)
	if err != nil {
		t.Fatal(err)
	}
	swap := cleared.WithWallReplacement()
	a2, err := domain.NewDeconstructionAction("swap", swap)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("deconstruct-plan", 1, []domain.Action{a0, a1, a2})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "deconstruct-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 3 {
		t.Fatal(got)
	}
	v0, _ := got[0].Deconstruction()
	v1, _ := got[1].Deconstruction()
	v2, _ := got[2].Deconstruction()
	if v0 != base || v1 != cleared || len(v1.ClearedGround()) != 2 || v1.ReplacesWithWall() || v2 != swap || !v2.ReplacesWithWall() {
		t.Fatal(v0, v1)
	}
}
