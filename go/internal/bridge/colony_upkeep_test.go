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

func flooringWire() *o.FlooringSection {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	terrain := func(name string, cleanliness float64, natural bool) *o.FloorTerrain {
		return &o.FloorTerrain{DefName: proto.String(name), Cleanliness: proto.Float64(cleanliness), PathCost: proto.Int32(0), Beauty: proto.Float64(0), Flammability: proto.Float64(0), Natural: proto.Bool(natural)}
	}
	return &o.FlooringSection{Outcome: &o.FlooringSection_Observed{Observed: &o.FlooringFacts{
		Rooms: []*o.FloorRoom{{RoomId: proto.String("7"), Role: proto.String("Kitchen"), Cells: []*o.FloorCell{
			{Cell: cell(10, 10), Terrain: proto.String("Soil")},
			{Cell: cell(11, 10), Terrain: proto.String("Soil"), Pending: proto.String("WoodPlankFloor")},
		}}},
		Terrains:     []*o.FloorTerrain{terrain("Soil", -1, true), terrain("WoodPlankFloor", 0, false)},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
	}}}
}

func TestDirectUpkeepFlooringBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	v := upkeepWire()
	v.Flooring = flooringWire()
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.FlooringFacts){
		func(f *o.FlooringFacts) { f.Rooms[0].Cells[0].Cell.X = proto.Int32(50) },
		func(f *o.FlooringFacts) { f.Rooms[0].Cells[0].Terrain = nil },
		func(f *o.FlooringFacts) { f.Rooms[0].Cells[0].Terrain = proto.String("Lava") },
		func(f *o.FlooringFacts) { f.Rooms[0].Cells[0].Pending = proto.String("") },
		func(f *o.FlooringFacts) { f.Rooms[0].Cells = append(f.Rooms[0].Cells, f.Rooms[0].Cells[0]) },
		func(f *o.FlooringFacts) { f.Rooms = append(f.Rooms, f.Rooms[0]) },
		func(f *o.FlooringFacts) { f.Rooms[0].RoomId = proto.String("") },
		func(f *o.FlooringFacts) { f.Rooms[0].Role = proto.String("") },
		func(f *o.FlooringFacts) { f.Terrains = append(f.Terrains, f.Terrains[0]) },
		func(f *o.FlooringFacts) { f.Terrains[0].Cleanliness = proto.Float64(math.NaN()) },
		func(f *o.FlooringFacts) { f.Terrains[0].Beauty = proto.Float64(math.Inf(1)) },
		func(f *o.FlooringFacts) { f.Terrains[0].PathCost = proto.Int32(-1) },
		func(f *o.FlooringFacts) { f.Terrains[0].DefName = nil },
		func(f *o.FlooringFacts) { f.Completeness = nil },
	} {
		v := upkeepWire()
		v.Flooring = flooringWire()
		mutate(v.Flooring.GetObserved())
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("accepted invalid flooring section")
		}
	}
	v = upkeepWire()
	v.Flooring = flooringWire()
	v.Issues = []*o.ReadIssue{{Field: proto.String("flooring")}}
	if err := validateDirectUpkeep(v, size, 3); err == nil {
		t.Fatal("accepted rows under a failed flooring section")
	}
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

func routesWire() *o.RoutesSection {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	return &o.RoutesSection{Outcome: &o.RoutesSection_Observed{Observed: &o.RoutesFacts{
		PawnIds: []string{"a", "b"},
		Facilities: []*o.RouteFacility{
			{
				Facility: &o.EntityRef{Id: proto.String("zone-3"), DefName: proto.String("Zone_Stockpile"), MapId: proto.Int32(3), Position: cell(10, 10)},
				Kind:     proto.String("stockpile"), Cell: cell(10, 10), RoomId: proto.String("7"),
				Travel: []*o.RouteTravel{
					{PawnId: proto.String("a"), Reachable: proto.Bool(false)},
					{PawnId: proto.String("b"), Reachable: proto.Bool(false)},
				},
				Breaches: []*o.RouteBreach{{Cell: cell(9, 10), Edifice: proto.String("Wall"), Distance: proto.Int32(4)}, {Cell: cell(10, 9), Edifice: proto.String("Wall"), Distance: proto.Int32(9), Pending: proto.String("Door")}},
			},
			{
				Facility: &o.EntityRef{Id: proto.String("Thing_Bed1"), DefName: proto.String("Bed"), MapId: proto.Int32(3), Position: cell(20, 20)},
				Kind:     proto.String("bed"), Cell: cell(20, 20),
				Travel: []*o.RouteTravel{{PawnId: proto.String("a"), Reachable: proto.Bool(true), PathCost: proto.Int32(120), PathCells: proto.Int32(9)}},
			},
		},
		Traffic:          []*o.TrafficCell{{Cell: cell(5, 5), Samples: proto.Uint32(30), Terrain: proto.String("Soil"), Home: proto.Bool(true)}},
		TrafficSamples:   proto.Uint32(200),
		TrafficSinceTick: proto.Int32(400),
		Completeness:     &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
	}}}
}

func TestDirectUpkeepRoutesBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	v := upkeepWire()
	v.Routes = routesWire()
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.RoutesFacts){
		func(f *o.RoutesFacts) { f.PawnIds = append(f.PawnIds, "a") },
		func(f *o.RoutesFacts) { f.PawnIds = append(f.PawnIds, "") },
		func(f *o.RoutesFacts) { f.Facilities[0].Facility.Id = proto.String("Thing_Bed1") },
		func(f *o.RoutesFacts) { f.Facilities[0].Facility.MapId = proto.Int32(4) },
		func(f *o.RoutesFacts) { f.Facilities[0].Kind = nil },
		func(f *o.RoutesFacts) { f.Facilities[0].Cell.X = proto.Int32(50) },
		func(f *o.RoutesFacts) { f.Facilities[0].RoomId = proto.String("") },
		func(f *o.RoutesFacts) { f.Facilities[0].Travel[0].PawnId = proto.String("zed") },
		func(f *o.RoutesFacts) { f.Facilities[0].Travel[1].PawnId = proto.String("a") },
		func(f *o.RoutesFacts) { f.Facilities[0].Travel[0].PathCost = proto.Int32(3) },
		func(f *o.RoutesFacts) { f.Facilities[1].Travel[0].PathCost = proto.Int32(-1) },
		func(f *o.RoutesFacts) { f.Facilities[1].Travel[0].PathCells = proto.Int32(-1) },
		func(f *o.RoutesFacts) { f.Facilities[0].Breaches[0].Cell.Z = proto.Int32(-1) },
		func(f *o.RoutesFacts) { f.Facilities[0].Breaches[0].Edifice = nil },
		func(f *o.RoutesFacts) { f.Facilities[0].Breaches[1].Pending = proto.String("") },
		func(f *o.RoutesFacts) { f.Facilities[0].Breaches[0].Distance = proto.Int32(-1) },
		func(f *o.RoutesFacts) { f.Facilities[0].Breaches[1].Cell = f.Facilities[0].Breaches[0].Cell },
		func(f *o.RoutesFacts) { f.Traffic[0].Cell.X = proto.Int32(50) },
		func(f *o.RoutesFacts) { f.Traffic[0].Terrain = proto.String("") },
		func(f *o.RoutesFacts) { f.Traffic = append(f.Traffic, f.Traffic[0]) },
		func(f *o.RoutesFacts) { f.TrafficSinceTick = proto.Int32(-1) },
		func(f *o.RoutesFacts) { f.Completeness = nil },
	} {
		v := upkeepWire()
		v.Routes = routesWire()
		mutate(v.Routes.GetObserved())
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("accepted invalid routes section")
		}
	}
	v = upkeepWire()
	v.Routes = routesWire()
	v.Issues = []*o.ReadIssue{{Field: proto.String("routes")}}
	if err := validateDirectUpkeep(v, size, 3); err == nil {
		t.Fatal("accepted rows under a failed routes section")
	}
}
