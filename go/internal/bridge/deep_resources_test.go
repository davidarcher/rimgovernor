package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestDeepResourcesRejectsMalformedCensus(t *testing.T) {
	for _, tc := range []struct {
		name   string
		change func(*o.DeepResourcesFacts)
	}{
		{"lump bound", func(f *o.DeepResourcesFacts) { f.Lumps = make([]*o.DeepResourceLump, 257) }},
		{"scanner bound", func(f *o.DeepResourcesFacts) { f.GroundScanners = make([]*o.MineralScannerState, 257) }},
		{"missing count", func(f *o.DeepResourcesFacts) { f.Lumps[0].Count = nil }},
		{"zero cells", func(f *o.DeepResourcesFacts) { f.Lumps[0].CellCount = proto.Uint32(0) }},
		{"impossible count", func(f *o.DeepResourcesFacts) { f.Lumps[0].Count = proto.Int64(200000) }},
		{"off map", func(f *o.DeepResourcesFacts) { f.Lumps[0].Centre.X = proto.Int32(-1) }},
		{"duplicate lump", func(f *o.DeepResourcesFacts) { f.Lumps = append(f.Lumps, f.Lumps[0]) }},
		{"duplicate scanner", func(f *o.DeepResourcesFacts) { f.LongRangeScanners = f.GroundScanners }},
		{"negative timing", func(f *o.DeepResourcesFacts) { f.GroundScanners[0].TicksToNextFind = proto.Int64(-1) }},
		{"unbuilt", func(f *o.DeepResourcesFacts) { f.GroundScanners[0].Built = proto.Bool(false) }},
		{"drill bound", func(f *o.DeepResourcesFacts) { f.Drills = append(f.Drills, make([]*o.DeepDrillState, 256)...) }},
		{"drill shares scanner id", func(f *o.DeepResourcesFacts) { f.Drills[0].BuildingId = proto.String("scanner") }},
		{"drill off map", func(f *o.DeepResourcesFacts) { f.Drills[0].Position.X = proto.Int32(-1) }},
		{"drill unknown depletion", func(f *o.DeepResourcesFacts) { f.Drills[0].Depleted = nil }},
		{"drill unknown designation", func(f *o.DeepResourcesFacts) { f.Drills[0].Designated = nil }},
		{"yielding drill without resource", func(f *o.DeepResourcesFacts) { f.Drills[0].Resource = nil }},
		{"yielding drill without remainder", func(f *o.DeepResourcesFacts) { f.Drills[0].Remaining = proto.Int64(0) }},
		{"depleted drill with deposit", func(f *o.DeepResourcesFacts) { f.Drills[0].Depleted = proto.Bool(true) }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			v := colonyFixture(t).GetObserved()
			f := &o.DeepResourcesFacts{Lumps: []*o.DeepResourceLump{{DefName: proto.String("Plasteel"), Count: proto.Int64(300), CellCount: proto.Uint32(3), Centre: &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}}}, GroundScanners: []*o.MineralScannerState{{BuildingId: proto.String("scanner"), DefName: proto.String("GroundPenetratingScanner"), Built: proto.Bool(true), Position: &c.Cell{X: proto.Int32(11), Z: proto.Int32(10)}}},
				Drills: []*o.DeepDrillState{{BuildingId: proto.String("drill"), DefName: proto.String("DeepDrill"), Position: &c.Cell{X: proto.Int32(12), Z: proto.Int32(10)}, Powered: proto.Bool(true), Depleted: proto.Bool(false), Resource: proto.String("Plasteel"), Remaining: proto.Int64(300), Designated: proto.Bool(false)}}}
			v.DeepResources = &o.DeepResourcesSection{Outcome: &o.DeepResourcesSection_Observed{Observed: f}}
			if err := ValidateColonyFacts(v, v.Context.Identity); err != nil {
				t.Fatal(err)
			}
			tc.change(f)
			if err := ValidateColonyFacts(v, v.Context.Identity); err == nil {
				t.Fatal("accepted malformed deep resources")
			}
		})
	}
}
