package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

func TestConstructionTierReadback(t *testing.T) {
	row := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("Frame_12"), Position: &c.Cell{X: proto.Int32(4), Z: proto.Int32(5)}}, BuildDefName: proto.String("Wall"), Stuff: proto.String("WoodLog"), Rotation: p.Rotation_ROTATION_NORTH.Enum(), Status: o.BuildingStatus_BUILDING_STATUS_FRAME.Enum(), Construction: &o.ConstructionState{Tier: proto.Int32(0)}}
	site, ok, err := constructionSite(row)
	if err != nil || !ok || site.Tier != domain.Known(domain.TierSurvive) {
		t.Fatalf("tier zero readback: %+v %v", site, err)
	}
	row.Construction.Tier = nil
	if site, _, err = constructionSite(row); err != nil || site.Tier != domain.Unknown[domain.ConstructionTier]() {
		t.Fatalf("absent tier became a rung: %+v %v", site, err)
	}
	row.Construction.Tier = proto.Int32(6)
	if _, _, err = constructionSite(row); err == nil {
		t.Fatal("out-of-ladder tier accepted")
	}
}
