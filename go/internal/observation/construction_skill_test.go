package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

func TestConstructionSkillReadbackPreservesUnknownAndPending(t *testing.T) {
	row := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("Frame_12"), Position: &c.Cell{X: proto.Int32(4), Z: proto.Int32(5)}}, BuildDefName: proto.String("Bed"), Stuff: proto.String("WoodLog"), Rotation: p.Rotation_ROTATION_NORTH.Enum(), Status: o.BuildingStatus_BUILDING_STATUS_FRAME.Enum(), Construction: &o.ConstructionState{QualitySensitive: proto.Bool(true), ResourcesComplete: proto.Bool(true), MinimumFinishingSkill: proto.Int32(9), NativeFinishingSkill: proto.Int32(0), EligibleFinishers: proto.Int32(0), FinishingBlocker: proto.String("no_qualified_builder")}}
	site, ok, err := constructionSite(row)
	if err != nil || !ok || site.ID != "Frame_12" || site.MinimumFinishingSkill != domain.Known(9) || site.EligibleFinishers != domain.Known(0) || site.FinishingBlocker != "no_qualified_builder" || site.ResourcesComplete != domain.Known(true) {
		t.Fatalf("readback: %+v %v", site, err)
	}
	row.Construction.MinimumFinishingSkill = nil
	site, _, err = constructionSite(row)
	if err != nil || site.MinimumFinishingSkill != domain.Unknown[int]() {
		t.Fatalf("absent minimum became zero: %+v %v", site, err)
	}
	row.Construction.MinimumFinishingSkill = proto.Int32(-1)
	if _, _, err = constructionSite(row); err == nil {
		t.Fatal("negative native minimum accepted")
	}
}

func TestConstructionCapabilityIsNativeAndIndependentOfDraft(t *testing.T) {
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("builder")}, Drafted: proto.Bool(true), ConstructionCapable: proto.Bool(true)}
	if got := workPawnRow(row); got.ConstructionAble != domain.Known(true) || got.Available != domain.Known(false) {
		t.Fatalf("drafted capable builder: %+v", got)
	}
	row.ConstructionCapable = nil
	if got := workPawnRow(row); got.ConstructionAble != domain.Unknown[bool]() {
		t.Fatalf("missing native capability was invented: %+v", got)
	}
}
