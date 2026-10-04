package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A choose_permit pawn setting (#1878) persists its pawn, faction and permit.
func TestChoosePermitSettingRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewChoosePermitSetting("pawn-3", "Empire", "CallMilitaryAidSmall")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewPawnSettingsAction("roy", value)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("roy-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
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
	if v, ok := got[0].PawnSettings(); !ok || v != value {
		t.Fatal(v, value)
	}
}
