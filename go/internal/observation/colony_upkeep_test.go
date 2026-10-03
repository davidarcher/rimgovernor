package observation

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestUpkeepProjectionPreservesSectionsAndFalsePresence(t *testing.T) {
	u := &o.UpkeepFacts{Fires: []*o.FireState{{Fire: bridge.NewRef("fire"), Home: proto.Bool(true)}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, err := policy.ReviewUpkeep(upkeepOf(t, v, bridge.Tables{}), policy.UpkeepHistory{}, nil)
	if err != nil || !r.Needs[0].Active || !r.Needs[0].Unsafe || r.Needs[1].Active {
		t.Fatal(r, err)
	}
	u.Fires[0].Home = proto.Bool(false)
	r, err = policy.ReviewUpkeep(upkeepOf(t, v, bridge.Tables{}), r.History, nil)
	if err != nil || r.Needs[0].Active {
		t.Fatal("false Home treated as absent", r, err)
	}
	u.Fires[0].Home = nil
	f := upkeepOf(t, v, bridge.Tables{})
	if _, known := f.Fires.Value(); known {
		t.Fatal("missing Home became known")
	}
	u.Fires = nil
	u.Issues = []*o.ReadIssue{{Field: proto.String("fires")}}
	f = upkeepOf(t, v, bridge.Tables{})
	if _, known := f.Fires.Value(); known {
		t.Fatal("failed empty census recovered")
	}
	if _, known := f.Items.Value(); !known {
		t.Fatal("unrelated census lost")
	}
	u.Items = []*o.UpkeepItem{{}}
	if _, known := upkeepOf(t, v, bridge.Tables{}).Items.Value(); known {
		t.Fatal("partial item became known")
	}
	if _, known := upkeepOf(t, &o.ColonyFactsSnapshot{}, bridge.Tables{}).Items.Value(); known {
		t.Fatal("absent section became empty")
	}
}

func TestUpkeepFilthCarriesRoomIdentity(t *testing.T) {
	u := &o.UpkeepFacts{Filth: []*o.FilthState{
		{Filth: bridge.NewRef("blood"), Home: proto.Bool(true), Thickness: proto.Uint32(2), RoomRole: proto.String("Kitchen"), Room: &c.Ref{Id: proto.String("7")}},
		{Filth: bridge.NewRef("dirt"), Home: proto.Bool(true), Thickness: proto.Uint32(1)},
	}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	rows, known := upkeepOf(t, v, heads(&o.EntityRef{Id: proto.String("blood"), DefName: proto.String("Filth_Blood")}, &o.EntityRef{Id: proto.String("dirt")})).Filth.Value()
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
	// Cleanliness, beauty and flammability are the catalog's terrain stats.
	tables := bridge.Tables{Catalog: itemCatalog(t, nil, map[string][3]float32{"Soil": {-1, -3, 0}, "WoodPlankFloor": {0, -3, 0}})}
	flooring := &o.FlooringFacts{
		Rooms: []*o.FloorRoom{{Room: &c.Ref{Id: proto.String("7")}, Role: proto.String("Kitchen"), Cells: []*o.FloorCell{
			{Cell: cell(10, 10), Terrain: proto.String("Soil")},
			{Cell: cell(11, 10), Terrain: proto.String("Soil"), Pending: proto.String("WoodPlankFloor")},
		}}, {Room: &c.Ref{Id: proto.String("8")}, Cells: []*o.FloorCell{{Cell: cell(20, 20), Terrain: proto.String("WoodPlankFloor")}}}},
	}
	u := &o.UpkeepFacts{Flooring: &o.FlooringSection{Outcome: &o.FlooringSection_Observed{Observed: flooring}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	f, known := upkeepOf(t, v, tables).Flooring.Value()
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
	if _, err := colonyUpkeep(v, bridge.Tables{Catalog: itemCatalog(t, nil, map[string][3]float32{"Soil": {-1, -3, 0}})}); err == nil || !strings.Contains(err.Error(), "flooring census") || !strings.Contains(err.Error(), "WoodPlankFloor") {
		t.Fatal("a terrain the catalog has no stats for was not a named error", err)
	}
	if _, err := colonyUpkeep(v, bridge.Tables{}); err == nil || !strings.Contains(err.Error(), "flooring census") {
		t.Fatal("flooring without a catalog was not a named error", err)
	}
	flooring.Rooms[0].Cells[0].Terrain = nil
	if _, known := upkeepOf(t, v, tables).Flooring.Value(); known {
		t.Fatal("unmeasured cell became known")
	}
	u.Flooring = nil
	if _, known := upkeepOf(t, v, tables).Flooring.Value(); known {
		t.Fatal("absent section became known")
	}
	u.Flooring = &o.FlooringSection{Outcome: &o.FlooringSection_Observed{Observed: &o.FlooringFacts{}}}
	if f, known := upkeepOf(t, v, tables).Flooring.Value(); !known || len(f.Rooms) != 0 {
		t.Fatal("empty census is a known census", f, known)
	}
}

func TestUpkeepProjectionDecodesLighting(t *testing.T) {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	lighting := &o.LightingFacts{
		WorkCells: []*o.WorkLightCell{{Bench: &c.Ref{Id: proto.String("stove")}, Cell: cell(10, 11), Glow: proto.Float64(0.1), Roofed: proto.Bool(true), Room: &c.Ref{Id: proto.String("7")}, LightSensitive: proto.Bool(true)}},
		Lamps:     []*o.LampState{{Building: &c.Ref{Id: proto.String("lamp")}, GlowRadius: proto.Float64(12), Lit: proto.Bool(false), Room: &c.Ref{Id: proto.String("7")}}},
	}
	lamp := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("lamp"), DefName: proto.String("StandingLamp"), Position: cell(12, 12)}, Service: &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(false), SwitchedOn: proto.Bool(true), BrokenDown: proto.Bool(false)}, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}
	u := &o.UpkeepFacts{Lighting: &o.LightingSection{Outcome: &o.LightingSection_Observed{Observed: lighting}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	if _, known := upkeepOf(t, v, bridge.Tables{}).Lighting.Value(); known {
		t.Fatal("an unresolved lamp reference became a known census")
	}
	f, known := upkeepOf(t, v, bridge.Tables{Buildings: buildingRows(lamp, &o.BuildingState{Building: &o.EntityRef{Id: proto.String("stove"), DefName: proto.String("FueledStove"), Position: cell(10, 10)}})}).Lighting.Value()
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
	if _, known := upkeepOf(t, v, bridge.Tables{}).Lighting.Value(); known {
		t.Fatal("unmeasured glow became known")
	}
	lighting.WorkCells[0].Glow = proto.Float64(0.1)
	lighting.Lamps[0].Lit = nil
	if _, known := upkeepOf(t, v, bridge.Tables{}).Lighting.Value(); known {
		t.Fatal("unmeasured lamp became known")
	}
	u.Lighting = nil
	if _, known := upkeepOf(t, v, bridge.Tables{}).Lighting.Value(); known {
		t.Fatal("absent section became known")
	}
	u.Lighting = &o.LightingSection{Outcome: &o.LightingSection_Observed{Observed: &o.LightingFacts{}}}
	if f, known := upkeepOf(t, v, bridge.Tables{}).Lighting.Value(); !known || len(f.WorkCells) != 0 {
		t.Fatal("empty census is a known census", f, known)
	}
	u.Issues = []*o.ReadIssue{{Field: proto.String("lighting")}}
	if _, known := upkeepOf(t, v, bridge.Tables{}).Lighting.Value(); known {
		t.Fatal("failed section became known")
	}
}

func routesWireFacts() *o.RoutesFacts {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	return &o.RoutesFacts{
		PawnIds: []string{"a", "b"},
		Facilities: []*o.RouteFacility{{
			Facility: &c.Ref{Id: proto.String("zone-3")},
			Kind:     o.RouteFacilityKind_ROUTE_FACILITY_KIND_STOCKPILE.Enum(), Cell: cell(10, 10), Room: &c.Ref{Id: proto.String("7")},
			Travel: []*o.RouteTravel{
				{PawnId: proto.String("a"), Reachable: proto.Bool(false)},
				{PawnId: proto.String("b"), Reachable: proto.Bool(true), PathCost: proto.Int32(120), PathCells: proto.Int32(9)},
			},
			Breaches: []*o.RouteBreach{{Cell: cell(9, 10), Edifice: proto.String("Wall"), Distance: proto.Int32(4), Pending: proto.String("Door")}},
		}},
		Traffic:          []*o.TrafficCell{{Cell: cell(5, 5), Samples: proto.Uint32(30), Terrain: proto.String("Soil"), Home: proto.Bool(true), Layer: o.TrafficLayer_TRAFFIC_LAYER_COLONIST}, {Cell: cell(6, 5), Samples: proto.Uint32(3), Terrain: proto.String("Lava"), Home: proto.Bool(false), Layer: o.TrafficLayer_TRAFFIC_LAYER_COLONIST}},
		TrafficSamples:   proto.Uint32(200),
		TrafficSinceTick: proto.Int32(400),
	}
}

func TestUpkeepProjectionDecodesRoutes(t *testing.T) {
	routes := routesWireFacts()
	u := &o.UpkeepFacts{Routes: &o.RoutesSection{Outcome: &o.RoutesSection_Observed{Observed: routes}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, known := upkeepOf(t, v, bridge.Tables{}).Routes.Value()
	if !known || len(r.Pawns) != 2 || len(r.Facilities) != 1 || len(r.Traffic) != 2 || r.TrafficSamples != 200 || r.TrafficSince != domain.Known(domain.Tick(400)) {
		t.Fatal(r, known)
	}
	f := r.Facilities[0]
	if f.ID != "zone-3" || f.Kind != "stockpile" || f.Cell != (domain.Cell{X: 10, Z: 10}) || f.Room != domain.Known("7") || len(f.Travel) != 2 || len(f.Breaches) != 1 {
		t.Fatal(f)
	}
	if f.Travel[0] != (policy.RouteTravel{Pawn: "a"}) || f.Travel[1] != (policy.RouteTravel{Pawn: "b", Reachable: true, Cost: domain.Known[int32](120), Cells: domain.Known[int32](9)}) {
		t.Fatal(f.Travel)
	}
	if f.Breaches[0] != (policy.RouteBreach{Cell: domain.Cell{X: 9, Z: 10}, Edifice: "Wall", Pending: "Door", Distance: 4}) {
		t.Fatal(f.Breaches)
	}
	if r.Traffic[0] != (policy.TrafficCell{Cell: domain.Cell{X: 5, Z: 5}, Samples: 30, Terrain: "Soil", Home: true, Layer: policy.TrafficColonist}) {
		t.Fatal(r.Traffic)
	}
	routes.Facilities[0].Travel[0].Reachable = nil
	if _, known := upkeepOf(t, v, bridge.Tables{}).Routes.Value(); known {
		t.Fatal("unmeasured reachability became known")
	}
	routes.Facilities[0].Travel[0].Reachable = proto.Bool(false)
	routes.Traffic[0].Home = nil
	if _, known := upkeepOf(t, v, bridge.Tables{}).Routes.Value(); known {
		t.Fatal("unmeasured traffic cell became known")
	}
	u.Routes = nil
	if _, known := upkeepOf(t, v, bridge.Tables{}).Routes.Value(); known {
		t.Fatal("absent section became known")
	}
}

func TestUpkeepProjectionJoinsTrafficIntoFlooring(t *testing.T) {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	flooring := &o.FlooringFacts{
		Rooms:    []*o.FloorRoom{{Room: &c.Ref{Id: proto.String("7")}, Cells: []*o.FloorCell{{Cell: cell(10, 10), Terrain: proto.String("Soil")}}}},
	}
	tables := bridge.Tables{Catalog: itemCatalog(t, nil, map[string][3]float32{"Soil": {-1, -3, 0}})}
	routes := routesWireFacts()
	u := &o.UpkeepFacts{Flooring: &o.FlooringSection{Outcome: &o.FlooringSection_Observed{Observed: flooring}}, Routes: &o.RoutesSection{Outcome: &o.RoutesSection_Observed{Observed: routes}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	f, known := upkeepOf(t, v, tables).Flooring.Value()
	// The traffic cell on a terrain the table does not name is left out.
	if !known || f.TrafficSamples != 200 || len(f.Traffic) != 1 || f.Traffic[0].Cell != (domain.Cell{X: 5, Z: 5}) {
		t.Fatal(f, known)
	}
	// An unmeasured routes census leaves flooring unknown too, as does a
	// routes read issue.
	routes.Traffic[0].Samples = nil
	if _, known := upkeepOf(t, v, tables).Flooring.Value(); known {
		t.Fatal("flooring known without its traffic evidence")
	}
	routes.Traffic[0].Samples = proto.Uint32(30)
	u.Issues = []*o.ReadIssue{{Field: proto.String("routes")}}
	u.Routes = nil
	if _, known := upkeepOf(t, v, tables).Flooring.Value(); known {
		t.Fatal("flooring known under a routes issue")
	}
	// A native without the routes section (older mod) still measures floors.
	u.Issues = nil
	if f, known := upkeepOf(t, v, tables).Flooring.Value(); !known || len(f.Traffic) != 0 {
		t.Fatal(f, known)
	}
}

// TestUpkeepItemsReadDefFactsFromCatalog (#1733): an item's base
// deterioration and medicine flag are the catalog's, not the frame's; a def the
// catalog cannot answer for is a named error.
func TestUpkeepItemsReadDefFactsFromCatalog(t *testing.T) {
	u := &o.UpkeepFacts{Items: []*o.UpkeepItem{{Item: bridge.NewRef("herb"), Count: proto.Int64(5), Roofed: proto.Bool(false), InStorage: proto.Bool(false), Forbidden: proto.Bool(false)}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	tables := heads(&o.EntityRef{Id: proto.String("herb"), DefName: proto.String("MedicineHerbal")})
	tables.Catalog = itemCatalog(t, map[string]itemFact{"MedicineHerbal": {deterioration: 2.5, medicine: true}}, nil)
	items, known := upkeepOf(t, v, tables).Items.Value()
	if !known || len(items) != 1 || items[0].Deterioration != 2.5 || !items[0].Medicine {
		t.Fatal(items, known)
	}
	tables.Catalog = itemCatalog(t, map[string]itemFact{"Steel": {}}, nil)
	if _, err := colonyUpkeep(v, tables); err == nil || !strings.Contains(err.Error(), "upkeep items census") || !strings.Contains(err.Error(), "MedicineHerbal") {
		t.Fatal("an item whose def the catalog lacks was not a named error", err)
	}
	if _, err := colonyUpkeep(v, heads(&o.EntityRef{Id: proto.String("herb"), DefName: proto.String("MedicineHerbal")})); err == nil || !strings.Contains(err.Error(), "upkeep items census") {
		t.Fatal("items without a catalog were not a named error", err)
	}
}

func upkeepOf(t *testing.T, v *o.ColonyFactsSnapshot, tables bridge.Tables) policy.UpkeepObservation {
	t.Helper()
	r, err := colonyUpkeep(v, tables)
	if err != nil {
		t.Fatal(err)
	}
	return r
}
