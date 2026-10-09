package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestConstructionTierIsAlwaysSentAndReTiersExactTarget(t *testing.T) {
	b, _ := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 4}, domain.North, "WoodLog")
	place, _ := domain.NewBuildingAction("place", b, domain.TierExpand)
	if wire, err := buildingAction(place); err != nil || wire.GetBuilding().Tier == nil || wire.GetBuilding().GetTier() != 4 {
		t.Fatalf("placement states its tier: %v %v", wire, err)
	}
	placed, err := place.WithTier(domain.TierComfort, "")
	if err != nil {
		t.Fatal(err)
	}
	if wire, err := buildingAction(placed); err != nil || wire.GetBuilding().GetTier() != 2 || wire.GetBuilding().ExistingTargetId != nil {
		t.Fatalf("tiered placement: %v %v", wire, err)
	}
	retier, err := place.WithTier(domain.TierSecure, "Frame_12")
	if err != nil {
		t.Fatal(err)
	}
	if wire, err := buildingAction(retier); err != nil || wire.GetBuilding().GetTier() != 5 || wire.GetBuilding().GetExistingTargetId() != "Frame_12" {
		t.Fatalf("set-tier: %v %v", wire, err)
	}
	if _, err = place.WithTier(domain.ConstructionTier(6), ""); err == nil {
		t.Fatal("tier above the ladder accepted")
	}
	if wire, _ := buildingAction(place); wire.GetBuilding().ReplaceWall != nil {
		t.Fatalf("a plain placement must not carry the replacement flag: %v", wire)
	}
	swap, err := place.WithWallReplacement()
	if err != nil {
		t.Fatal(err)
	}
	if wire, err := buildingAction(swap); err != nil || !wire.GetBuilding().GetReplaceWall() || wire.GetBuilding().GetTier() != 4 {
		t.Fatalf("wall replacement: %v %v", wire, err)
	}
	if _, err = retier.WithWallReplacement(); err == nil {
		t.Fatal("a replacement may not adopt an existing target")
	}
}
