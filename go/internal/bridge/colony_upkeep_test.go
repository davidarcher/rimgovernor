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
	return &o.UpkeepFacts{Items: []*o.UpkeepItem{{Item: entity("item"), Count: proto.Int64(2), Roofed: proto.Bool(false), InStorage: proto.Bool(false), Forbidden: proto.Bool(false), Medicine: proto.Bool(false), BaseDeteriorationRate: proto.Float64(1)}}, Structures: []*o.UpkeepStructure{{Building: &o.BuildingState{Building: entity("wall"), HitPoints: proto.Int32(5), MaxHitPoints: proto.Int32(10)}, Home: proto.Bool(true), RepairPriority: proto.Int32(1)}}, Fires: []*o.FireState{{Fire: entity("fire"), Home: proto.Bool(true), Size: proto.Float64(.5)}}, Filth: []*o.FilthState{{Filth: entity("filth"), Home: proto.Bool(true), Thickness: proto.Uint32(1), RoomRole: proto.String("Kitchen"), RoomId: proto.String("7")}}}
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

func lightingWire() *o.LightingSection {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	entity := func(id string, x, z int32) *o.EntityRef {
		return &o.EntityRef{Id: proto.String(id), DefName: proto.String("Thing"), MapId: proto.Int32(3), Position: cell(x, z)}
	}
	return &o.LightingSection{Outcome: &o.LightingSection_Observed{Observed: &o.LightingFacts{
		WorkCells:    []*o.WorkLightCell{{Bench: entity("stove", 10, 10), Cell: cell(10, 11), Glow: proto.Float64(0.2), Roofed: proto.Bool(true), RoomId: proto.String("7")}},
		Lamps:        []*o.LampState{{Building: &o.BuildingState{Building: entity("lamp", 12, 12), Service: &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(true), SwitchedOn: proto.Bool(true), BrokenDown: proto.Bool(false), Fuel: proto.Float64(1), TargetFuel: proto.Float64(2), OutOfFuel: proto.Bool(false), AllowedFuelDefs: []string{"WoodLog"}}}, GlowRadius: proto.Float64(10), Lit: proto.Bool(true), RoomId: proto.String("7")}},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
	}}}
}

func TestDirectUpkeepLightingBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	v := upkeepWire()
	v.Lighting = lightingWire()
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.LightingFacts){
		func(l *o.LightingFacts) { l.WorkCells[0].Glow = proto.Float64(1.5) },
		func(l *o.LightingFacts) { l.WorkCells[0].Glow = proto.Float64(math.NaN()) },
		func(l *o.LightingFacts) { l.WorkCells[0].Cell.X = proto.Int32(50) },
		func(l *o.LightingFacts) { l.WorkCells[0].Bench.MapId = proto.Int32(4) },
		func(l *o.LightingFacts) { l.WorkCells = append(l.WorkCells, l.WorkCells[0]) },
		func(l *o.LightingFacts) { l.WorkCells[0].RoomId = proto.String("") },
		func(l *o.LightingFacts) { l.Lamps = append(l.Lamps, l.Lamps[0]) },
		func(l *o.LightingFacts) { l.Lamps[0].GlowRadius = proto.Float64(-1) },
		func(l *o.LightingFacts) { l.Lamps[0].Building.Service = nil },
		func(l *o.LightingFacts) { l.Lamps[0].Building.Service.PowerNetId = proto.String("net") },
		func(l *o.LightingFacts) { l.Lamps[0].Building.HitPoints = proto.Int32(1) },
		func(l *o.LightingFacts) { l.Lamps[0].Building.Service.Fuel = proto.Float64(-1) },
		func(l *o.LightingFacts) { l.Lamps[0].Building.Service.AllowedFuelDefs = []string{""} },
		func(l *o.LightingFacts) { l.Completeness = nil },
	} {
		v := upkeepWire()
		v.Lighting = lightingWire()
		mutate(v.Lighting.GetObserved())
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("accepted invalid lighting section")
		}
	}
	v = upkeepWire()
	v.Lighting = lightingWire()
	v.Issues = []*o.ReadIssue{{Field: proto.String("lighting")}}
	if err := validateDirectUpkeep(v, size, 3); err == nil {
		t.Fatal("accepted rows under a failed lighting section")
	}
}
