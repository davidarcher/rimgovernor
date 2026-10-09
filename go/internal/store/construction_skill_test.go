package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestConstructionSkillJournalRoundTrip(t *testing.T) {
	ctx := context.Background()
	s, path, _ := goalFixture(t)
	b, err := domain.NewBuilding("Bed", domain.Cell{X: 3, Z: 4}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("quality", b, domain.TierExpand)
	if err == nil {
		a, err = a.WithFinishingSkill(9, "Blueprint_12")
	}
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("quality-plan", 1, []domain.Action{a})
	if err == nil {
		err = s.CreatePlan(ctx, plan)
	}
	if err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	got, err := s.LoadPlan(ctx, plan.ID())
	if err != nil || got.Spec.Actions()[0] != a {
		t.Fatalf("action lost exact target/minimum: %+v %v", got, err)
	}
}
