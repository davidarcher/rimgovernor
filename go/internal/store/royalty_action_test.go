package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A royalty write (#1606) persists its pawn, faction, verb and permit.
func TestRoyaltyActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewRoyalty("pawn-3", "Empire", domain.RoyaltyChoosePermit, "CallMilitaryAidSmall")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewRoyaltyAction("roy", value)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("roy-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "roy-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].Royalty(); !ok || v != value {
		t.Fatal(v, value)
	}
}
