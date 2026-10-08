package store

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestHackAndGiveActionsRoundTripWithoutLosingPreconditions(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	for i, enabled := range []bool{false, true} {
		h, _ := domain.NewHackDesignation("Terminal_1", enabled)
		a, _ := domain.NewHackDesignationAction(domain.ActionID(fmt.Sprintf("hack-%d", i)), h)
		p, _ := domain.NewPlan(domain.PlanID(fmt.Sprintf("hack-plan-%d", i)), 1, []domain.Action{a})
		if err := s.CreatePlan(ctx, p); err != nil {
			t.Fatal(err)
		}
		loaded, err := s.LoadPlan(ctx, p.ID())
		if err != nil || loaded.Spec.Actions()[0] != a {
			t.Fatal(loaded, err)
		}
	}
	g, _ := domain.NewGiveItem("Pawn_1", "Pawn_2", "Silver", 17)
	a, _ := domain.NewGiveItemAction("give", g)
	p, _ := domain.NewPlan("give-plan", 1, []domain.Action{a})
	if err := s.CreatePlan(ctx, p); err != nil {
		t.Fatal(err)
	}
	loaded, err := s.LoadPlan(ctx, p.ID())
	if err != nil || loaded.Spec.Actions()[0] != a {
		t.Fatal(loaded, err)
	}
	if _, err = s.db.ExecContext(ctx, `UPDATE actions SET definition='{"Definition":"Silver","ExpectedRemaining":17,"Unknown":1}' WHERE id='give'`); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LoadPlan(ctx, p.ID()); err == nil {
		t.Fatal("corrupt gift payload accepted")
	}
}
