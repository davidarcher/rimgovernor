package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestConstructionTierJournalRoundTripAndCanonicalForm(t *testing.T) {
	ctx := context.Background()
	s, path, _ := goalFixture(t)
	b, _ := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 4}, domain.North, "WoodLog")
	placed, _ := domain.NewBuildingAction("placed", b, domain.TierExpand)
	placed, err := placed.WithTier(domain.TierSurvive, "")
	if err != nil {
		t.Fatal(err)
	}
	retier, _ := domain.NewBuildingAction("retier", b, domain.TierExpand)
	retier, err = retier.WithTier(domain.TierSecure, "Frame_12")
	if err != nil {
		t.Fatal(err)
	}
	both, _ := domain.NewBuildingAction("both", b, domain.TierExpand)
	both, _ = both.WithFinishingSkill(9, "Frame_13")
	both, err = both.WithTier(domain.TierProduce, "Frame_13")
	if err != nil {
		t.Fatal(err)
	}
	plain, _ := domain.NewBuildingAction("plain", b, domain.TierExpand)
	actions := []domain.Action{placed, retier, both, plain}
	plan, err := domain.NewPlan("tier-plan", 1, actions)
	if err == nil {
		err = s.CreatePlan(ctx, plan)
	}
	if err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	got, err := s.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	for i, want := range actions {
		if got.Spec.Actions()[i] != want {
			t.Fatalf("action %d lost its setting: %+v", i, got.Spec.Actions()[i])
		}
	}
	if tier, known := got.Spec.Actions()[0].Tier().Value(); !known || tier != domain.TierSurvive {
		t.Fatalf("tier zero read as unset: %v %v", tier, known)
	}
	if _, err = s.db.ExecContext(ctx, `UPDATE actions SET zone_payload=CAST('{"Tier":5,"Target":"Frame_12","Extra":1}' AS BLOB) WHERE id='retier'`); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LoadPlan(ctx, plan.ID()); err == nil {
		t.Fatal("noncanonical construction setting loaded")
	}
}
