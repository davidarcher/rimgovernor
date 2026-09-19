package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"os"
	"testing"
)

func TestPoliciesUseZonesNotAggregateZoneFields(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	reply := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, reply); err != nil {
		t.Fatal(err)
	}
	v := reply.GetObserved()
	// Existing captures still decode, but the aggregate cannot supply zone facts.
	v.FoodStorage = proto.Bool(true)
	p, err := DecodeColony(reply, Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))})
	if err != nil {
		t.Fatal(err)
	}
	_, storageKnown := p.Facts.FoodStorage.Value()
	_, growingKnown := p.Facts.GrowingCells.Value()
	_, tokenKnown := p.ZoneMapToken.Value()
	if storageKnown || growingKnown || len(p.Farms) > 0 || tokenKnown {
		t.Fatal("aggregate supplied zone policy facts")
	}
	farm := &o.FarmFacts{ZoneId: proto.String("Zone_1"), Crop: proto.String("Rice"), UsableCells: proto.Uint32(20), GrowingCells: proto.Uint32(10), EdibleCrop: proto.Bool(true)}
	p.Definitions = []PlanningDefinition{{Name: "Rice", GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(1.0)}}
	applyZones(&p, bridge.ZonesRead{Rows: []*o.ZoneState{{Id: farm.ZoneId, Farm: farm, FoodStorage: proto.Bool(false)}, {Id: proto.String("Zone_2"), FoodStorage: proto.Bool(true)}}, MapSnapshot: &o.SnapshotRef{Token: proto.String("zone-map")}})
	if p.Facts.FoodStorage != domain.Known(true) || p.Facts.GrowingCells != domain.Known(int64(10)) || len(p.Farms) != 1 || p.Farms[0].ID != "Zone_1" || p.ZoneMapToken != domain.Known("zone-map") {
		t.Fatal(p)
	}
	applyZones(&p, bridge.ZonesRead{Rows: []*o.ZoneState{}})
	if p.Facts.FoodStorage != domain.Known(false) || p.Facts.GrowingCells != domain.Known(int64(0)) || len(p.Farms) != 0 {
		t.Fatal("empty zone census not applied")
	}
}
