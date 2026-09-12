package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func upkeepWire() *o.UpkeepFacts {
	entity := func(id string) *o.EntityRef {
		return &o.EntityRef{Id: proto.String(id), DefName: proto.String("Thing"), MapId: proto.Int32(3), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}
	}
	return &o.UpkeepFacts{Items: []*o.UpkeepItem{{Item: entity("item"), Count: proto.Int64(2), Roofed: proto.Bool(false), InStorage: proto.Bool(false), Forbidden: proto.Bool(false), Medicine: proto.Bool(false), BaseDeteriorationRate: proto.Float64(1)}}, Structures: []*o.UpkeepStructure{{Building: &o.BuildingState{Building: entity("wall"), HitPoints: proto.Int32(5), MaxHitPoints: proto.Int32(10)}, Home: proto.Bool(true), RepairPriority: proto.Int32(1)}}, Fires: []*o.FireState{{Fire: entity("fire"), Home: proto.Bool(true), Size: proto.Float64(.5)}}, Filth: []*o.FilthState{{Filth: entity("filth"), Home: proto.Bool(true), Thickness: proto.Uint32(1)}}}
}
func TestDirectUpkeepBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(upkeepWire(), size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.UpkeepFacts){
		func(v *o.UpkeepFacts) { v.Items[0].Item.MapId = proto.Int32(4) },
		func(v *o.UpkeepFacts) { v.Items[0].Item.Position.X = proto.Int32(50) },
		func(v *o.UpkeepFacts) { v.Items = append(v.Items, v.Items[0]) },
		func(v *o.UpkeepFacts) { v.Items = []*o.UpkeepItem{{}} },
		func(v *o.UpkeepFacts) { v.Issues = []*o.ReadIssue{{Field: proto.String("fires")}} },
		func(v *o.UpkeepFacts) { v.Fires[0].Size = proto.Float64(math.NaN()) },
		func(v *o.UpkeepFacts) { v.Structures[0].Building.HitPoints = proto.Int32(11) },
		func(v *o.UpkeepFacts) { v.Filth[0].Cleanable = proto.Bool(true) },
	} {
		v := upkeepWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("invalid upkeep accepted", v)
		}
	}
}
