package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A pawn settings action (#1299) persists its pawn and hostility response.
func TestPawnSettingsActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewHostilitySetting("Human7", domain.HostilityIgnore)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewPawnSettingsAction("hostility", value)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("settings-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "settings-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].PawnSettings(); !ok || v != value {
		t.Fatal(v, value)
	}
}
