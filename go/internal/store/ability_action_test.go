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
	psycast, err := domain.PsycastSource("Skip")
	if err != nil {
		t.Fatal(err)
	}
	pawnTarget, _ := domain.AbilityPawnTarget("Human13")
	thingTarget, _ := domain.AbilityThingTarget("Thing9")
	var want []domain.Ability
	var actions []domain.Action
	// Permit and psycast sources (#1610) share the row; the psycast key is
	// "psycast:<abilityDef>" in the definition column.
	for i, target := range []domain.AbilityTarget{cell, domain.NoAbilityTarget(), pawnTarget, thingTarget, cell, domain.NoAbilityTarget()} {
		src := source
		if i >= 2 {
			src = psycast
		}
		ability, err := domain.NewAbility("Human12", src, target)
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
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
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
