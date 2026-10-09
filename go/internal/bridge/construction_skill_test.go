package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestConstructionSettingIsExactTargetIntent(t *testing.T) {
	b, _ := domain.NewBuilding("Bed", domain.Cell{X: 3, Z: 4}, domain.North, "WoodLog")
	a, _ := domain.NewBuildingAction("quality", b, domain.TierExpand)
	a, _ = a.WithFinishingSkill(9, "Frame_12")
	wire, err := buildingAction(a)
	if err != nil || wire.GetBuilding().GetMinimumFinishingSkill() != 9 || wire.GetBuilding().GetExistingTargetId() != "Frame_12" {
		t.Fatal(wire, err)
	}
}
