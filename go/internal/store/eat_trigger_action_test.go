package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An eat_trigger pawn setting persists its pawn and level.
func TestEatTriggerSettingRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	lowered, err := domain.NewEatTriggerSetting("Human3", 0.125)
	if err != nil {
		t.Fatal(err)
	}
	vanilla, err := domain.NewEatTriggerSetting("Human4", 0.3)
	if err != nil {
		t.Fatal(err)
	}
	var actions []domain.Action
	for id, value := range map[domain.ActionID]domain.PawnSettings{"eat-low": lowered, "eat-vanilla": vanilla} {
		a, err := domain.NewPawnSettingsAction(id, value)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	p, err := domain.NewPlan("eat-plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "eat-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 2 {
		t.Fatal(got)
	}
	for _, a := range got {
		v, ok := a.PawnSettings()
		if _, isTrigger := v.EatTrigger(); !ok || !isTrigger || v != lowered && v != vanilla {
			t.Fatal(v)
		}
	}
}
