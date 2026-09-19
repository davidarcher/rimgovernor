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

func TestFoodChannelsPresenceThroughColonyDecode(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	reply := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, reply); err != nil {
		t.Fatal(err)
	}
	identity := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	decode := func() ColonyProjection {
		t.Helper()
		p, e := DecodeColony(reply, identity)
		if e != nil {
			t.Fatal(e)
		}
		return p
	}
	if _, known := decode().FoodChannels.Value(); known {
		t.Fatal("missing section became known")
	}
	f := &o.FoodChannelsFacts{Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
		Gatherable:     []*o.GatherableAnimal{{PawnId: proto.String("cow"), Race: proto.String("Cow"), Fullness: proto.Float64(0), Resource: proto.String("Milk"), HandlerReachable: proto.Bool(false)}, {PawnId: proto.String("dog"), Race: proto.String("LabradorRetriever")}},
		EggLayer:       []*o.EggLayerAnimal{{PawnId: proto.String("hen"), Race: proto.String("Chicken"), CanLayNow: proto.Bool(false), Progress: proto.Float64(0)}, {PawnId: proto.String("cow"), Race: proto.String("Cow")}},
		PasteDispenser: []*o.PasteDispenser{{BuildingId: proto.String("paste"), Powered: proto.Bool(false), HopperNutrition: proto.Float64(0), AdjacentRoomId: proto.String("12")}},
		PollutedCells:  proto.Uint32(0), Forage: []*o.ForagePlant{{DefName: proto.String("Plant_Berry"), GrowingTwelfths: []int32{3, 4}, GrowingNow: proto.Bool(false)}}}
	reply.GetObserved().FoodChannels = &o.FoodChannelsSection{Outcome: &o.FoodChannelsSection_Observed{Observed: f}}
	p, known := decode().FoodChannels.Value()
	if !known {
		t.Fatal("observed census unknown")
	}
	if _, known = p.FishableWater.Value(); known {
		t.Fatal("Core-only fishing became known empty")
	}
	if v, k := p.Gatherable[0].Fullness.Value(); !k || v != 0 {
		t.Fatal("known empty udder lost")
	}
	if v, k := p.Gatherable[0].Resource.Value(); !k || v != "Milk" {
		t.Fatal("resource lost")
	}
	if v, k := p.Gatherable[0].HandlerReachable.Value(); !k || v {
		t.Fatal("false handler reachability lost")
	}
	if _, k := p.Gatherable[1].Fullness.Value(); k {
		t.Fatal("missing gatherable comp became zero")
	}
	if _, k := p.EggLayer[1].CanLayNow.Value(); k {
		t.Fatal("missing egg comp became false")
	}
	if v, k := p.EggLayer[0].Progress.Value(); !k || v != 0 {
		t.Fatal("known egg progress lost")
	}
	if v, k := p.PasteDispenser[0].HopperNutrition.Value(); !k || v != 0 {
		t.Fatal("empty hopper lost")
	}
	if v, k := p.PasteDispenser[0].Powered.Value(); !k || v {
		t.Fatal("unpowered dispenser lost")
	}
	if v, k := p.PollutedCells.Value(); !k || v != 0 {
		t.Fatal("known clean window lost")
	}
	if len(p.Forage) != 1 || len(p.Forage[0].GrowingTwelfths) != 2 {
		t.Fatal("forage season lost")
	}
	f.FishableWater = &o.FishableWater{FishingResearched: proto.Bool(false), Regions: []*o.FishableRegion{{Root: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}, Population: proto.Float64(10), MaxPopulation: proto.Float64(20), Zoned: proto.Bool(false), CellCount: proto.Uint32(2), Reachable: proto.Bool(true)}}}
	p, _ = decode().FoodChannels.Value()
	water, k := p.FishableWater.Value()
	if !k || len(water.Regions) != 1 {
		t.Fatal("water region lost")
	}
	if n, k := water.Regions[0].Population.Value(); !k || n != 10 {
		t.Fatal("population lost")
	}
	reply.GetObserved().FoodChannels = &o.FoodChannelsSection{Outcome: &o.FoodChannelsSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("unreadable")}}}
	if _, known := decode().FoodChannels.Value(); known {
		t.Fatal("unavailable census became known")
	}
}
