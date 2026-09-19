package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func TestFoodChannelsRejectsMalformedCensus(t *testing.T) {
	for _, tc := range []struct {
		name   string
		change func(*o.FoodChannelsFacts)
	}{
		{"missing completeness", func(f *o.FoodChannelsFacts) { f.Completeness = nil }},
		{"filtered", func(f *o.FoodChannelsFacts) { f.Completeness.Filtered = proto.Uint64(1) }},
		{"animal bound", func(f *o.FoodChannelsFacts) { f.EggLayer = make([]*o.EggLayerAnimal, 257) }},
		{"negative fullness", func(f *o.FoodChannelsFacts) { f.Gatherable[0].Fullness = proto.Float64(-0.1) }},
		{"excess fullness", func(f *o.FoodChannelsFacts) { f.Gatherable[0].Fullness = proto.Float64(1.1) }},
		{"nan", func(f *o.FoodChannelsFacts) { f.Gatherable[0].Fullness = proto.Float64(math.NaN()) }},
		{"duplicate animal", func(f *o.FoodChannelsFacts) { f.Gatherable = append(f.Gatherable, f.Gatherable[0]) }},
		{"pollution bounds", func(f *o.FoodChannelsFacts) { f.PollutedCells = proto.Uint32(2026) }},
		{"season bounds", func(f *o.FoodChannelsFacts) {
			f.Forage = []*o.ForagePlant{{DefName: proto.String("Berry"), GrowingTwelfths: []int32{12}}}
		}},
		{"season duplicate", func(f *o.FoodChannelsFacts) {
			f.Forage = []*o.ForagePlant{{DefName: proto.String("Berry"), GrowingTwelfths: []int32{1, 1}}}
		}},
		{"fish bounds", func(f *o.FoodChannelsFacts) {
			f.FishableWater = &o.FishableWater{Regions: []*o.FishableRegion{{Root: &c.Cell{X: proto.Int32(-1), Z: proto.Int32(1)}}}}
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			v := colonyFixture(t).GetObserved()
			f := &o.FoodChannelsFacts{Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}, Gatherable: []*o.GatherableAnimal{{PawnId: proto.String("cow"), Race: proto.String("Cow")}}}
			v.FoodChannels = &o.FoodChannelsSection{Outcome: &o.FoodChannelsSection_Observed{Observed: f}}
			if err := ValidateColonyFacts(v, v.Context.Identity); err != nil {
				t.Fatal(err)
			}
			tc.change(f)
			if err := ValidateColonyFacts(v, v.Context.Identity); err == nil {
				t.Fatal("accepted malformed channels")
			}
		})
	}
}
