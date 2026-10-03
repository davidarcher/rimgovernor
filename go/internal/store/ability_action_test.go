package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An ability (#1607) persists its pawn, source and target through every target
// shape the permit source takes.
func TestAbilityActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	source, err := domain.PermitSource("Empire", "CallMilitaryAidSmall")
	if err != nil {
		t.Fatal(err)
	}
	cell, err := domain.AbilityCellTarget(domain.Cell{X: 12, Z: 31})
	if err != nil {
		t.Fatal(err)
	}
	var want []domain.Ability
	var actions []domain.Action
	for i, target := range []domain.AbilityTarget{cell, domain.NoAbilityTarget()} {
		ability, err := domain.NewAbility("Human12", source, target)
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewAbilityAction(domain.ActionID("ability"+string(rune('a'+i))), ability)
		if err != nil {
			t.Fatal(err)
		}
		want, actions = append(want, ability), append(actions, a)
	}
	p, err := domain.NewPlan("ability-plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "ability-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != len(want) {
		t.Fatal(got)
	}
	for i := range want {
		if v, ok := got[i].Ability(); !ok || v != want[i] {
			t.Fatal(i, v, want[i])
		}
	}
}
