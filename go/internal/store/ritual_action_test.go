package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A ritual command persists its pawn, ritual and verb.
func TestRitualActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewRitual("pawn-3", domain.RitualBestowing, domain.RitualStart)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewRitualAction("rit", value)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("rit-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "rit-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].Ritual(); !ok || v != value {
		t.Fatal(v, value)
	}
}
