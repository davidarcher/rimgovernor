package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestEventLootBoundary(t *testing.T) {
	v := &o.ColonyFactsSnapshot{Context: &c.ObservationContext{Identity: &c.Identity{MapId: proto.Int32(1)}}, MapSize: &o.MapSize{Width: proto.Uint32(100), Height: proto.Uint32(100)}}
	row := &o.LootItem{Item: &o.EntityRef{Id: proto.String("steel-1"), DefName: proto.String("Steel"), MapId: proto.Int32(1), Position: &c.Cell{X: proto.Int32(70), Z: proto.Int32(80)}}, Forbidden: proto.Bool(true), SafeToHaul: proto.Bool(true)}
	census := &o.LootCensus{Items: []*o.LootItem{row}}
	v.EventLoot = &o.LootSection{Outcome: &o.LootSection_Observed{Observed: census}}
	if err := validateEventLoot(v); err != nil {
		t.Fatal(err)
	}
	row.SafeToHaul = nil
	if err := validateEventLoot(v); err != nil {
		t.Fatal("unknown safety refused", err)
	}
	row.SafeToHaul = proto.Bool(false)
	census.Items = append(census.Items, row)
	if validateEventLoot(v) == nil {
		t.Fatal("duplicate accepted")
	}
}
