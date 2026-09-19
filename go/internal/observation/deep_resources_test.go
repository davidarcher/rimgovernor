package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"os"
	"testing"
)

func TestDeepResourcesProjectionPresence(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	id := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	for _, section := range []*o.DeepResourcesSection{nil, {Outcome: &o.DeepResourcesSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("unreadable")}}}} {
		r.GetObserved().DeepResources = section
		p, err := DecodeColony(r, id)
		if err != nil {
			t.Fatal(err)
		}
		if _, known := p.DeepResources.Value(); known {
			t.Fatal("absent/unavailable census became known")
		}
	}
	f := &o.DeepResourcesFacts{}
	r.GetObserved().DeepResources = &o.DeepResourcesSection{Outcome: &o.DeepResourcesSection_Observed{Observed: f}}
	p, err := DecodeColony(r, id)
	if err != nil {
		t.Fatal(err)
	}
	if value, known := p.DeepResources.Value(); !known || len(value.Lumps) != 0 || len(value.GroundScanners) != 0 {
		t.Fatal("empty census lost", p.DeepResources)
	}
	f.Lumps = []*o.DeepResourceLump{{DefName: proto.String("Plasteel"), Count: proto.Int64(300), Centre: &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}, CellCount: proto.Uint32(3)}}
	f.GroundScanners = []*o.MineralScannerState{{BuildingId: proto.String("scanner"), DefName: proto.String("GroundPenetratingScanner"), Position: &c.Cell{X: proto.Int32(11), Z: proto.Int32(10)}, Built: proto.Bool(true), Powered: proto.Bool(false)}}
	f.LongRangeScanners = []*o.MineralScannerState{{BuildingId: proto.String("long"), DefName: proto.String("LongRangeMineralScanner"), Position: &c.Cell{X: proto.Int32(12), Z: proto.Int32(10)}, Built: proto.Bool(true), Working: proto.Bool(false), TicksToNextFind: proto.Int64(0), TargetResource: proto.String("Gold")}}
	p, err = DecodeColony(r, id)
	if err != nil {
		t.Fatal(err)
	}
	v, known := p.DeepResources.Value()
	if !known || len(v.Lumps) != 1 || v.Lumps[0].Count != 300 || v.Lumps[0].CellCount != 3 || v.Lumps[0].Centre.X != 10 {
		t.Fatal(v)
	}
	if value, known := v.GroundScanners[0].Powered.Value(); !known || value {
		t.Fatal("known false lost")
	}
	if _, known := v.GroundScanners[0].Working.Value(); known {
		t.Fatal("unknown working became false")
	}
	if _, known := v.GroundScanners[0].TicksToNextFind.Value(); known {
		t.Fatal("unknown timing became zero")
	}
	if value, known := v.LongRangeScanners[0].TicksToNextFind.Value(); !known || value != 0 {
		t.Fatal("known zero lost")
	}
	if value, known := v.LongRangeScanners[0].TargetResource.Value(); !known || value != "Gold" {
		t.Fatal("target lost")
	}
}
