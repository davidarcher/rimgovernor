package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A sterilize order (#1631) persists as a husbandry action with no argument
// and, as it puts a doctor to work, holds a development slot.
func TestHusbandrySterilizeActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewHusbandry("Thing_Goat1", domain.HusbandrySterilize, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewHusbandryAction("sterilize", value)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("sterilize-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "sterilize", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "sterilize-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].Husbandry(); !ok || v != value {
		t.Fatal(v, value)
	}
}
