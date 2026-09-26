package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func TestMoveBuildingOperationAndEffect(t *testing.T) {
	m, _ := domain.NewMoveBuilding("Thing_Bed7", "Bed", domain.Cell{X: 5, Z: 9}, domain.West)
	install := moveBuildingOperation(m).GetInstallBuilding()
	if install.GetPackedOrInner().GetEntityId() != "Thing_Bed7" || install.GetPackedOrInner().ExpectedSnapshotToken != nil || install.GetDestination().GetX() != 5 || install.GetDestination().GetZ() != 9 || install.GetRotation() != p.Rotation_ROTATION_WEST {
		t.Fatal(install)
	}
	effect := func(stage r.InstallationStage, blueprint string) *r.EffectEvidence {
		return &r.EffectEvidence{Effect: &r.EffectEvidence_Installation{Installation: &r.InstallationEffect{InnerThingId: proto.String("Thing_Bed7"), DefName: proto.String("Bed"),
			Cell: &c.Cell{X: proto.Int32(5), Z: proto.Int32(9)}, Rotation: p.Rotation_ROTATION_WEST.Enum(), Stage: stage.Enum(), BlueprintId: proto.String(blueprint)}}}
	}
	queued, installed := r.InstallationStage_INSTALLATION_STAGE_QUEUED, r.InstallationStage_INSTALLATION_STAGE_INSTALLED
	if moveBuildingEffect(effect(queued, "Blueprint_Install1"), m, queued) != nil || moveBuildingEffect(effect(installed, ""), m, installed) != nil {
		t.Fatal("valid move evidence refused")
	}
	if moveBuildingEffect(effect(queued, ""), m, queued) == nil {
		t.Fatal("queued move without its blueprint accepted")
	}
	if moveBuildingEffect(effect(queued, "Blueprint_Install1"), m, installed) == nil {
		t.Fatal("queued evidence accepted as completion")
	}
	if moveBuildingEffect(effect(installed, ""), m, 0) == nil {
		t.Fatal("failure reporting installation accepted")
	}
	other, _ := domain.NewMoveBuilding("Thing_Bed7", "Bed", domain.Cell{X: 5, Z: 9}, domain.North)
	if moveBuildingEffect(effect(installed, ""), other, installed) == nil {
		t.Fatal("evidence at another rotation accepted")
	}
}
