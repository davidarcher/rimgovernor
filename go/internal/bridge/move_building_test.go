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
	install := moveBuildingOperation(m, false).GetInstallBuilding()
	if install.GetPackedOrInner().GetEntityId() != "Thing_Bed7" || install.GetPackedOrInner().ExpectedSnapshotToken != nil || install.GetDestination().GetX() != 5 || install.GetDestination().GetZ() != 9 || install.GetRotation() != p.Rotation_ROTATION_WEST {
		t.Fatal(install)
	}
	effect := func(stage r.InstallationStage, blueprint string) *r.EffectEvidence {
		return &r.EffectEvidence{Effect: &r.EffectEvidence_Installation{Installation: &r.InstallationEffect{InnerThingId: proto.String("Thing_Bed7"), DefName: proto.String("Bed"),
			Cell: &c.Cell{X: proto.Int32(5), Z: proto.Int32(9)}, Rotation: p.Rotation_ROTATION_WEST.Enum(), Stage: stage.Enum(), BlueprintId: proto.String(blueprint)}}}
	}
	queued, installed := r.InstallationStage_INSTALLATION_STAGE_QUEUED, r.InstallationStage_INSTALLATION_STAGE_INSTALLED
	if moveBuildingEffect(effect(queued, "Blueprint_Install1"), m, false, queued) != nil || moveBuildingEffect(effect(installed, ""), m, false, installed) != nil {
		t.Fatal("valid move evidence refused")
	}
	if moveBuildingEffect(effect(queued, ""), m, false, queued) == nil {
		t.Fatal("queued move without its blueprint accepted")
	}
	if moveBuildingEffect(effect(queued, "Blueprint_Install1"), m, false, installed) == nil {
		t.Fatal("queued evidence accepted as completion")
	}
	if moveBuildingEffect(effect(installed, ""), m, false, 0) == nil {
		t.Fatal("failure reporting installation accepted")
	}
	other, _ := domain.NewMoveBuilding("Thing_Bed7", "Bed", domain.Cell{X: 5, Z: 9}, domain.North)
	if moveBuildingEffect(effect(installed, ""), other, false, installed) == nil {
		t.Fatal("evidence at another rotation accepted")
	}
}

func TestUninstallOperationAndEffect(t *testing.T) {
	m, _ := domain.NewMoveBuilding("Thing_Bed7", "Bed", domain.Cell{X: 5, Z: 9}, domain.West)
	if u := moveBuildingOperation(m, true).GetUninstall(); u.GetTarget().GetEntityId() != "Thing_Bed7" || u.GetTarget().ExpectedSnapshotToken != nil {
		t.Fatal(u)
	}
	effect := func(stage r.InstallationStage, packed string) *r.EffectEvidence {
		return &r.EffectEvidence{Effect: &r.EffectEvidence_Installation{Installation: &r.InstallationEffect{InnerThingId: proto.String("Thing_Bed7"), DefName: proto.String("Bed"),
			Cell: &c.Cell{X: proto.Int32(5), Z: proto.Int32(9)}, Rotation: p.Rotation_ROTATION_WEST.Enum(), Stage: stage.Enum(), PackedThingId: proto.String(packed)}}}
	}
	placeable, queued, installed := r.InstallationStage_INSTALLATION_STAGE_PLACEABLE, r.InstallationStage_INSTALLATION_STAGE_QUEUED, r.InstallationStage_INSTALLATION_STAGE_INSTALLED
	uq, packed := r.InstallationStage_INSTALLATION_STAGE_UNINSTALL_QUEUED, r.InstallationStage_INSTALLATION_STAGE_PACKED
	// Callers pass the move's position; the uninstall maps it.
	if moveBuildingEffect(effect(placeable, ""), m, true, placeable) != nil || moveBuildingEffect(effect(uq, ""), m, true, queued) != nil ||
		moveBuildingEffect(effect(packed, "Thing_MinifiedThing3"), m, true, installed) != nil || moveBuildingEffect(effect(installed, ""), m, true, 0) != nil {
		t.Fatal("valid uninstall evidence refused")
	}
	if moveBuildingEffect(effect(packed, ""), m, true, installed) == nil {
		t.Fatal("packed uninstall without its packed item accepted")
	}
	if moveBuildingEffect(effect(queued, ""), m, true, queued) == nil || moveBuildingEffect(effect(uq, ""), m, true, installed) == nil {
		t.Fatal("wrong stage accepted")
	}
	if moveBuildingEffect(effect(packed, "Thing_MinifiedThing3"), m, true, 0) == nil {
		t.Fatal("failure reporting packing accepted")
	}
}
