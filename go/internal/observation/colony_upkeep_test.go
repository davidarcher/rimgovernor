package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestUpkeepProjectionPreservesSectionsAndFalsePresence(t *testing.T) {
	u := &o.UpkeepFacts{Fires: []*o.FireState{{Fire: &o.EntityRef{Id: proto.String("fire")}, Home: proto.Bool(true)}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, err := policy.ReviewUpkeep(colonyUpkeep(v), policy.UpkeepHistory{}, nil)
	if err != nil || !r.Needs[0].Active || !r.Needs[0].Unsafe || r.Needs[1].Active {
		t.Fatal(r, err)
	}
	u.Fires[0].Home = proto.Bool(false)
	r, err = policy.ReviewUpkeep(colonyUpkeep(v), r.History, nil)
	if err != nil || r.Needs[0].Active {
		t.Fatal("false Home treated as absent", r, err)
	}
	u.Fires[0].Home = nil
	f := colonyUpkeep(v)
	if _, known := f.Fires.Value(); known {
		t.Fatal("missing Home became known")
	}
	u.Fires = nil
	u.Issues = []*o.ReadIssue{{Field: proto.String("fires")}}
	f = colonyUpkeep(v)
	if _, known := f.Fires.Value(); known {
		t.Fatal("failed empty census recovered")
	}
	if _, known := f.Items.Value(); !known {
		t.Fatal("unrelated census lost")
	}
	u.Items = []*o.UpkeepItem{{}}
	if _, known := colonyUpkeep(v).Items.Value(); known {
		t.Fatal("partial item became known")
	}
	if _, known := colonyUpkeep(&o.ColonyFactsSnapshot{}).Items.Value(); known {
		t.Fatal("absent section became empty")
	}
}

func TestUpkeepFilthCarriesRoomIdentity(t *testing.T) {
	u := &o.UpkeepFacts{Filth: []*o.FilthState{
		{Filth: &o.EntityRef{Id: proto.String("blood"), DefName: proto.String("Filth_Blood")}, Home: proto.Bool(true), Thickness: proto.Uint32(2), RoomRole: proto.String("Kitchen"), RoomId: proto.String("7")},
		{Filth: &o.EntityRef{Id: proto.String("dirt")}, Home: proto.Bool(true), Thickness: proto.Uint32(1)},
	}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	rows, known := colonyUpkeep(v).Filth.Value()
	if !known || len(rows) != 2 {
		t.Fatal(rows, known)
	}
	if id, ok := rows[0].RoomID.Value(); !ok || id != "7" || rows[0].Definition != "Filth_Blood" || rows[0].Room != "Kitchen" {
		t.Fatal(rows[0])
	}
	if _, ok := rows[1].RoomID.Value(); ok {
		t.Fatal("outdoor filth gained a room", rows[1])
	}
}

func TestUpkeepProjectionDecodesFlooring(t *testing.T) {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	terrain := func(name string, cleanliness float64, natural bool) *o.FloorTerrain {
		return &o.FloorTerrain{DefName: proto.String(name), Cleanliness: proto.Float64(cleanliness), PathCost: proto.Int32(2), Beauty: proto.Float64(-3), Flammability: proto.Float64(0), Natural: proto.Bool(natural)}
	}
	flooring := &o.FlooringFacts{
		Rooms: []*o.FloorRoom{{RoomId: proto.String("7"), Role: proto.String("Kitchen"), Cells: []*o.FloorCell{
			{Cell: cell(10, 10), Terrain: proto.String("Soil")},
			{Cell: cell(11, 10), Terrain: proto.String("Soil"), Pending: proto.String("WoodPlankFloor")},
		}}, {RoomId: proto.String("8"), Cells: []*o.FloorCell{{Cell: cell(20, 20), Terrain: proto.String("WoodPlankFloor")}}}},
		Terrains: []*o.FloorTerrain{terrain("Soil", -1, true), terrain("WoodPlankFloor", 0, false)},
	}
	u := &o.UpkeepFacts{Flooring: &o.FlooringSection{Outcome: &o.FlooringSection_Observed{Observed: flooring}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	f, known := colonyUpkeep(v).Flooring.Value()
	if !known || len(f.Rooms) != 2 || len(f.Terrains) != 2 {
		t.Fatal(f, known)
	}
	r := f.Rooms[0]
	if r.ID != "7" || r.Role != domain.Known(policy.RoomRoleKitchen) || len(r.Cells) != 2 || r.Cells[0] != (policy.FloorCell{Cell: domain.Cell{X: 10, Z: 10}, Terrain: "Soil"}) || r.Cells[1].Pending != "WoodPlankFloor" {
		t.Fatal(r)
	}
	if _, ok := f.Rooms[1].Role.Value(); ok {
		t.Fatal("roleless room gained a role", f.Rooms[1])
	}
	if f.Terrains["Soil"] != (policy.FloorTerrain{Cleanliness: -1, Beauty: -3, PathCost: 2, Natural: true}) || f.Terrains["WoodPlankFloor"].Natural {
		t.Fatal(f.Terrains)
	}
	flooring.Terrains[0].Natural = nil
	if _, known := colonyUpkeep(v).Flooring.Value(); known {
		t.Fatal("unmeasured terrain became known")
	}
	flooring.Terrains[0].Natural = proto.Bool(true)
	flooring.Rooms[0].Cells[0].Terrain = nil
	if _, known := colonyUpkeep(v).Flooring.Value(); known {
		t.Fatal("unmeasured cell became known")
	}
	u.Flooring = nil
	if _, known := colonyUpkeep(v).Flooring.Value(); known {
		t.Fatal("absent section became known")
	}
	u.Flooring = &o.FlooringSection{Outcome: &o.FlooringSection_Observed{Observed: &o.FlooringFacts{}}}
	if f, known := colonyUpkeep(v).Flooring.Value(); !known || len(f.Rooms) != 0 {
		t.Fatal("empty census is a known census", f, known)
	}
}

func TestUpkeepProjectionDecodesLighting(t *testing.T) {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	lighting := &o.LightingFacts{
		WorkCells: []*o.WorkLightCell{{Bench: &o.EntityRef{Id: proto.String("stove"), DefName: proto.String("FueledStove"), Position: cell(10, 10)}, Cell: cell(10, 11), Glow: proto.Float64(0.1), Roofed: proto.Bool(true), RoomId: proto.String("7"), LightSensitive: proto.Bool(true)}},
		Lamps:     []*o.LampState{{Building: &o.BuildingState{Building: &o.EntityRef{Id: proto.String("lamp"), DefName: proto.String("StandingLamp"), Position: cell(12, 12)}, Service: &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(false), SwitchedOn: proto.Bool(true), BrokenDown: proto.Bool(false)}}, GlowRadius: proto.Float64(12), Lit: proto.Bool(false), RoomId: proto.String("7")}},
	}
	u := &o.UpkeepFacts{Lighting: &o.LightingSection{Outcome: &o.LightingSection_Observed{Observed: lighting}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	f, known := colonyUpkeep(v).Lighting.Value()
	if !known || len(f.WorkCells) != 1 || len(f.Lamps) != 1 {
		t.Fatal(f, known)
	}
	w := f.WorkCells[0]
	if w.Bench != "stove" || w.Definition != "FueledStove" || w.Cell != (domain.Cell{X: 10, Z: 11}) || w.Glow != 0.1 || !w.Roofed || !w.LightSensitive {
		t.Fatal(w)
	}
	if room, ok := w.Room.Value(); !ok || room != "7" {
		t.Fatal(w)
	}
	l := f.Lamps[0]
	if l.ID != "lamp" || l.Definition != "StandingLamp" || l.Cell != (domain.Cell{X: 12, Z: 12}) || l.Radius != 12 || l.Lit {
		t.Fatal(l)
	}
	if powered, ok := l.Powered.Value(); !ok || powered {
		t.Fatal(l)
	}
	if _, ok := l.OutOfFuel.Value(); ok {
		t.Fatal("fuel invented", l)
	}
	lighting.WorkCells[0].Glow = nil
	if _, known := colonyUpkeep(v).Lighting.Value(); known {
		t.Fatal("unmeasured glow became known")
	}
	lighting.WorkCells[0].Glow = proto.Float64(0.1)
	lighting.Lamps[0].Lit = nil
	if _, known := colonyUpkeep(v).Lighting.Value(); known {
		t.Fatal("unmeasured lamp became known")
	}
	u.Lighting = nil
	if _, known := colonyUpkeep(v).Lighting.Value(); known {
		t.Fatal("absent section became known")
	}
	u.Lighting = &o.LightingSection{Outcome: &o.LightingSection_Observed{Observed: &o.LightingFacts{}}}
	if f, known := colonyUpkeep(v).Lighting.Value(); !known || len(f.WorkCells) != 0 {
		t.Fatal("empty census is a known census", f, known)
	}
	u.Issues = []*o.ReadIssue{{Field: proto.String("lighting")}}
	if _, known := colonyUpkeep(v).Lighting.Value(); known {
		t.Fatal("failed section became known")
	}
}

func routesWireFacts() *o.RoutesFacts {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	return &o.RoutesFacts{
		PawnIds: []string{"a", "b"},
		Facilities: []*o.RouteFacility{{
			Facility: &o.EntityRef{Id: proto.String("zone-3"), DefName: proto.String("Zone_Stockpile"), MapId: proto.Int32(3), Position: cell(10, 10)},
			Kind:     proto.String("stockpile"), Cell: cell(10, 10), RoomId: proto.String("7"),
			Travel: []*o.RouteTravel{
				{PawnId: proto.String("a"), Reachable: proto.Bool(false)},
				{PawnId: proto.String("b"), Reachable: proto.Bool(true), PathCost: proto.Int32(120), PathCells: proto.Int32(9)},
			},
			Breaches: []*o.RouteBreach{{Cell: cell(9, 10), Edifice: proto.String("Wall"), Distance: proto.Int32(4), Pending: proto.String("Door")}},
		}},
		Traffic:          []*o.TrafficCell{{Cell: cell(5, 5), Samples: proto.Uint32(30), Terrain: proto.String("Soil"), Home: proto.Bool(true)}, {Cell: cell(6, 5), Samples: proto.Uint32(3), Terrain: proto.String("Lava"), Home: proto.Bool(false)}},
		TrafficSamples:   proto.Uint32(200),
		TrafficSinceTick: proto.Int32(400),
	}
}

func TestUpkeepProjectionDecodesRoutes(t *testing.T) {
	routes := routesWireFacts()
	u := &o.UpkeepFacts{Routes: &o.RoutesSection{Outcome: &o.RoutesSection_Observed{Observed: routes}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, known := colonyUpkeep(v).Routes.Value()
	if !known || len(r.Pawns) != 2 || len(r.Facilities) != 1 || len(r.Traffic) != 2 || r.TrafficSamples != 200 || r.TrafficSince != domain.Known(domain.Tick(400)) {
		t.Fatal(r, known)
	}
	f := r.Facilities[0]
	if f.ID != "zone-3" || f.Definition != "Zone_Stockpile" || f.Kind != "stockpile" || f.Cell != (domain.Cell{X: 10, Z: 10}) || f.Room != domain.Known("7") || len(f.Travel) != 2 || len(f.Breaches) != 1 {
		t.Fatal(f)
	}
	if f.Travel[0] != (policy.RouteTravel{Pawn: "a"}) || f.Travel[1] != (policy.RouteTravel{Pawn: "b", Reachable: true, Cost: domain.Known[int32](120), Cells: domain.Known[int32](9)}) {
		t.Fatal(f.Travel)
	}
	if f.Breaches[0] != (policy.RouteBreach{Cell: domain.Cell{X: 9, Z: 10}, Edifice: "Wall", Pending: "Door", Distance: 4}) {
		t.Fatal(f.Breaches)
	}
	if r.Traffic[0] != (policy.TrafficCell{Cell: domain.Cell{X: 5, Z: 5}, Samples: 30, Terrain: "Soil", Home: true}) {
		t.Fatal(r.Traffic)
	}
	routes.Facilities[0].Travel[0].Reachable = nil
	if _, known := colonyUpkeep(v).Routes.Value(); known {
		t.Fatal("unmeasured reachability became known")
	}
	routes.Facilities[0].Travel[0].Reachable = proto.Bool(false)
	routes.Traffic[0].Home = nil
	if _, known := colonyUpkeep(v).Routes.Value(); known {
		t.Fatal("unmeasured traffic cell became known")
	}
	u.Routes = nil
	if _, known := colonyUpkeep(v).Routes.Value(); known {
		t.Fatal("absent section became known")
	}
}

func TestUpkeepProjectionJoinsTrafficIntoFlooring(t *testing.T) {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	flooring := &o.FlooringFacts{
		Rooms:    []*o.FloorRoom{{RoomId: proto.String("7"), Cells: []*o.FloorCell{{Cell: cell(10, 10), Terrain: proto.String("Soil")}}}},
		Terrains: []*o.FloorTerrain{{DefName: proto.String("Soil"), Cleanliness: proto.Float64(-1), PathCost: proto.Int32(2), Beauty: proto.Float64(-3), Flammability: proto.Float64(0), Natural: proto.Bool(true)}},
	}
	routes := routesWireFacts()
	u := &o.UpkeepFacts{Flooring: &o.FlooringSection{Outcome: &o.FlooringSection_Observed{Observed: flooring}}, Routes: &o.RoutesSection{Outcome: &o.RoutesSection_Observed{Observed: routes}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	f, known := colonyUpkeep(v).Flooring.Value()
	// The traffic cell on a terrain the table does not name is left out.
	if !known || f.TrafficSamples != 200 || len(f.Traffic) != 1 || f.Traffic[0].Cell != (domain.Cell{X: 5, Z: 5}) {
		t.Fatal(f, known)
	}
	// An unmeasured routes census leaves flooring unknown too, as does a
	// routes read issue.
	routes.Traffic[0].Samples = nil
	if _, known := colonyUpkeep(v).Flooring.Value(); known {
		t.Fatal("flooring known without its traffic evidence")
	}
	routes.Traffic[0].Samples = proto.Uint32(30)
	u.Issues = []*o.ReadIssue{{Field: proto.String("routes")}}
	u.Routes = nil
	if _, known := colonyUpkeep(v).Flooring.Value(); known {
		t.Fatal("flooring known under a routes issue")
	}
	// A native without the routes section (older mod) still measures floors.
	u.Issues = nil
	if f, known := colonyUpkeep(v).Flooring.Value(); !known || len(f.Traffic) != 0 {
		t.Fatal(f, known)
	}
}
