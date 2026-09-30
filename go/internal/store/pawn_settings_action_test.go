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
	tend, err := domain.NewSelfTendSetting("Human7", false)
	if err != nil {
		t.Fatal(err)
	}
	b, err := domain.NewPawnSettingsAction("self-tend", tend)
	if err != nil {
		t.Fatal(err)
	}
	rename, err := domain.NewNicknameSetting("Human8", "Bob")
	if err != nil {
		t.Fatal(err)
	}
	n, err := domain.NewPawnSettingsAction("nickname", rename)
	if err != nil {
		t.Fatal(err)
	}
	care, err := domain.NewMedicalCareSetting("Human9", domain.CareBest)
	if err != nil {
		t.Fatal(err)
	}
	c, err := domain.NewPawnSettingsAction("care", care)
	if err != nil {
		t.Fatal(err)
	}
	carryValue, err := domain.NewMedicineCarrySetting("Human7", 0)
	if err != nil {
		t.Fatal(err)
	}
	carry, err := domain.NewPawnSettingsAction("carry", carryValue)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("settings-plan", 1, []domain.Action{a, b, n, c, carry})
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
	if len(got) != 5 {
		t.Fatal(got)
	}
	if v, ok := got[0].PawnSettings(); !ok || v != value {
		t.Fatal(v, value)
	}
	if v, ok := got[1].PawnSettings(); !ok || v != tend {
		t.Fatal(v, tend)
	}
	if v, ok := got[4].PawnSettings(); !ok || v != carryValue {
		t.Fatal(v, carryValue)
	}
	if v, ok := got[2].PawnSettings(); !ok || v != rename {
		t.Fatal(v, rename)
	}
	if v, ok := got[3].PawnSettings(); !ok || v != care {
		t.Fatal(v, care)
	}
}
