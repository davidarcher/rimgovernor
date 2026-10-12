package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// hotYear is a desert's twelfth means (C): the year sits far above the living
// band's top with the afternoon swing on top.
var hotYear = []float64{36, 37, 39, 41, 43, 44, 44, 43, 42, 40, 38, 36}

// stoveHeat is an electric stove's CompProperties_HeatPusher: 3 heat a second
// at any temperature. furnaceHeat is 120 a second, what the recorded
// AtmosphericHeater and GeothermalVent push and what a room of a dozen
// smelters comes to.
var (
	stoveHeat   = HeatSource{HeatPerSecond: 3, MinC: -99999, MaxC: 99999}
	furnaceHeat = HeatSource{HeatPerSecond: 120, MinC: -99999, MaxC: 99999}
	ventYes     = domain.Known(true)
)

// ventCatalog is the cluster catalog with the recorded climate model, the
// human's living band and a neighbour held at heldC (the heater's default
// target is 21).
func ventCatalog(heat HeatSource, heldC float64) StationCatalog {
	cat := clusterCatalog()
	cat.Climate = testClimate()
	cat.Climate.Living, cat.Climate.HeldC = Band{MinC: 16, MaxC: 26}, heldC
	stove := cat.Stations["Stove"]
	stove.Heat = heat
	cat.Stations["Stove"] = stove
	return cat
}

// ventTiling tiles a kitchen (group 0) and a dining room (group 1) side by side,
// a link door in the wall they share, and a freezer (group 2) apart from them.
func ventTiling(t *testing.T, curve []float64, available domain.Fact[bool], heat HeatSource) TileResult {
	t.Helper()
	return ventTilingHeld(t, curve, available, heat, 21)
}

func ventTilingHeld(t *testing.T, curve []float64, available domain.Fact[bool], heat HeatSource, heldC float64) TileResult {
	t.Helper()
	return ventTilingBuilt(t, curve, available, heat, heldC, testClimate().Build)
}

// ventTilingBuilt is ventTilingHeld with the catalog's build values given.
func ventTilingBuilt(t *testing.T, curve []float64, available domain.Fact[bool], heat HeatSource, heldC float64, build ClimateBuildValue) TileResult {
	t.Helper()
	cat := ventCatalog(heat, heldC)
	cat.Climate.Build = build
	group := func(id int, role RoomRole, purpose StationPurpose, def string, req ...EnvRequirement) RoomGroup {
		return RoomGroup{ID: id, Role: role, Requires: req, Area: 20, Stations: []GroupStation{{Node: nodeID(def, purpose), Def: def, Purpose: purpose, Count: 1}}}
	}
	groups := []RoomGroup{
		group(0, RoomRoleKitchen, PurposeWork, "Stove"),
		group(1, RoomRoleDiningRoom, PurposeDine, "Table"),
		group(2, RoomRoleStoreroom, PurposeStoreCold, "ColdShelf", EnvCold),
	}
	groups[0].Links = []GroupLink{{To: 1, Weight: 10}}
	groups[1].Links = []GroupLink{{To: 0, Weight: 10}}
	emb := Embedding{Blocks: []EmbeddedBlock{
		{Group: 0, Interior: Rectangle{X: 60, Z: 60, Width: 5, Height: 4}},
		{Group: 1, Interior: Rectangle{X: 66, Z: 60, Width: 5, Height: 4}},
		{Group: 2, Interior: Rectangle{X: 60, Z: 75, Width: 5, Height: 4}},
	}}
	res, err := TileEmbedding(TileInput{
		Groups: groups, Embedding: emb, Catalog: cat, Zones: Zone(plainScoreSurvey()), Survey: plainScoreSurvey(),
		OutdoorCurve: domain.Known(slices.Clone(curve)), FullRotRateC: 10, SteelValue: 1.9, VentAvailable: available,
	})
	if err != nil {
		t.Fatal(err)
	}
	return res
}

func ventsOf(res TileResult) []PlannedSite { return res.Plan.LiveSites(NodeVent) }

func ventsWhy(res TileResult, reason string) []PlannedSite {
	return slices.DeleteFunc(ventsOf(res), func(s PlannedSite) bool { return s.Reason != reason })
}

func decisionFor(res TileResult, group int, reason string) bool {
	return slices.ContainsFunc(res.Decisions, func(d TileDecision) bool { return d.Group == group && d.Reason == reason })
}

// The recorded probe: a vent costs about 195 W-equivalent to build at the
// layout's rates, so it wins only where it saves more power than that. A
// stove (3 a second) never does; a furnace room (120 a second) does on a cold
// map; the freezer never does (nothing gives off heat inside it).
func TestTileVentsWinWhereTheHeatInsideOutrunsAClosedDoor(t *testing.T) {
	for name, curve := range map[string][]float64{"sea ice": seaIce, "temperate": temperate, "hot": hotYear} {
		res := ventTiling(t, curve, ventYes, stoveHeat)
		if len(ventsOf(res)) != 0 || !decisionFor(res, 0, ReasonTileVentNotCheaper) {
			t.Errorf("%s kitchen with a stove: vents %v, decisions %+v", name, ventsOf(res), res.Decisions)
		}
	}
	ice := ventTiling(t, seaIce, ventYes, furnaceHeat)
	if got := ventsWhy(ice, ReasonTileVent); len(got) < 1 || !decisionFor(ice, 0, ReasonTileVent) || !ice.Plan.Valid() {
		t.Fatalf("sea ice furnace room: %d outdoor vents, decisions %+v", len(got), ice.Decisions)
	}
	mild := ventTiling(t, temperate, ventYes, HeatSource{HeatPerSecond: 300, MinC: -99999, MaxC: 99999})
	if got := ventsWhy(mild, ReasonTileVent); len(got) < 2 || !mild.Plan.Valid() {
		t.Errorf("temperate 300 a second: %d outdoor vents", len(got))
	}
	if res := ventTiling(t, hotYear, ventYes, furnaceHeat); len(ventsOf(res)) != 0 {
		t.Errorf("hot map: vents %v", ventsOf(res))
	}
	for name, curve := range map[string][]float64{"sea ice": seaIce, "temperate": temperate, "hot": hotYear} {
		res := ventTiling(t, curve, ventYes, furnaceHeat)
		if decisionFor(res, 2, ReasonTileVent) || decisionFor(res, 2, ReasonTileVentLink) {
			t.Errorf("%s freezer got a vent: %+v", name, res.Decisions)
		}
	}
}

// A vent between two rooms stands in the wall they share, beside the link door.
// The neighbour is held at the heater's default target (21 C), too near the
// kitchen's band to be worth a vent, so a link vent needs a contrived held
// temperature. When no temperature makes one pay, the test only checks that the
// ones placed stand beside the door.
func TestTileLinkVentStandsBesideTheLinkDoor(t *testing.T) {
	heat := HeatSource{HeatPerSecond: 60, MinC: -99999, MaxC: 99999}
	if got := ventsWhy(ventTiling(t, temperate, ventYes, heat), ReasonTileVentLink); len(got) != 0 {
		t.Error("a neighbour held at 21 C took a link vent")
	}
	for _, heldC := range []float64{-20, 0, 10} {
		res := ventTilingHeld(t, seaIce, ventYes, furnaceHeat, heldC)
		if len(res.Plan.Links) == 0 {
			t.Fatal("no link door")
		}
		door := res.Plan.Links[0].Cell
		for _, v := range ventsWhy(res, ReasonTileVentLink) {
			if c := v.Origin; abs32(c.X-door.X)+abs32(c.Z-door.Z) != 1 || c.X != door.X {
				t.Errorf("held %v: vent %v is not beside the door %v in its wall", heldC, c, door)
			}
		}
		if !res.Plan.Valid() {
			t.Errorf("held %v: plan invalid", heldC)
		}
	}
}

// Without the research the vent is an absent option: none is placed and the
// decision names why, where the vent would have won. The same holds while
// availability is unknown.
func TestTileNoVentWithoutResearch(t *testing.T) {
	for name, avail := range map[string]domain.Fact[bool]{"unresearched": domain.Known(false), "unknown": domain.Unknown[bool]()} {
		res := ventTiling(t, seaIce, avail, furnaceHeat)
		if len(ventsOf(res)) != 0 || !decisionFor(res, 0, ReasonTileVentUnavailable) {
			t.Errorf("%s: vents %v, decisions %+v", name, ventsOf(res), res.Decisions)
		}
	}
}

// With no heat inside, a kitchen's closed room floats to the outdoors with or
// without a vent: no vent pays for itself.
func TestTileNoVentWithoutHeatInside(t *testing.T) {
	res := ventTiling(t, seaIce, ventYes, HeatSource{})
	if len(ventsOf(res)) != 0 {
		t.Errorf("a kitchen with no stove heat got vents %v", ventsOf(res))
	}
}

// A climate the model cannot price leaves vents out and says so.
func TestTileVentClimateUnknown(t *testing.T) {
	group := RoomGroup{ID: 0, Role: RoomRoleKitchen, Area: 20, Stations: []GroupStation{{Node: nodeID("Stove", PurposeWork), Def: "Stove", Purpose: PurposeWork, Count: 1}}}
	emb := Embedding{Blocks: []EmbeddedBlock{{Group: 0, Interior: Rectangle{X: 60, Z: 60, Width: 5, Height: 4}}}}
	res, err := TileEmbedding(TileInput{Groups: []RoomGroup{group}, Embedding: emb, Catalog: ventCatalog(furnaceHeat, 21), Zones: Zone(plainScoreSurvey()), Survey: plainScoreSurvey()})
	if err != nil {
		t.Fatal(err)
	}
	if len(ventsOf(res)) != 0 || !slices.ContainsFunc(res.Decisions, func(d TileDecision) bool { return d.Reason == ReasonTileVentClimateUnknown }) {
		t.Errorf("vents %v, decisions %+v", ventsOf(res), res.Decisions)
	}
}

func TestTileVentsAreDeterministic(t *testing.T) {
	a := ventTiling(t, seaIce, ventYes, furnaceHeat)
	b := ventTiling(t, seaIce, ventYes, furnaceHeat)
	if len(ventsOf(a)) == 0 || !reflect.DeepEqual(a.Plan.Sites, b.Plan.Sites) || !reflect.DeepEqual(a.Decisions, b.Decisions) {
		t.Error("the same input tiled other vents")
	}
}

// The cooler and the vents want the same outside wall cells: a group with wall
// stations leaves the cells they need, so the vents it takes are bounded by
// what the ring leaves.
func TestTileVentsLeaveWallCellsForWallStations(t *testing.T) {
	stove := GroupStation{Node: nodeID("Stove", PurposeWork), Def: "Stove", Purpose: PurposeWork, Count: 1}
	cooler := func(n int) GroupStation {
		return GroupStation{Node: nodeID("Cooler", PurposeCool), Def: "Cooler", Purpose: PurposeCool, Count: n}
	}
	count := func(stations ...GroupStation) int {
		g := RoomGroup{ID: 0, Role: RoomRoleKitchen, Area: 20, Stations: stations}
		emb := Embedding{Blocks: []EmbeddedBlock{{Group: 0, Interior: Rectangle{X: 60, Z: 60, Width: 5, Height: 4}}}}
		res, err := TileEmbedding(TileInput{Groups: []RoomGroup{g}, Embedding: emb, Catalog: ventCatalog(HeatSource{HeatPerSecond: 300, MinC: -99999, MaxC: 99999}, 21),
			Zones: Zone(plainScoreSurvey()), Survey: plainScoreSurvey(), OutdoorCurve: domain.Known(slices.Clone(temperate)), FullRotRateC: 10, SteelValue: 1.9, VentAvailable: ventYes})
		if err != nil {
			t.Fatal(err)
		}
		return len(ventsOf(res))
	}
	free, held := count(stove), count(stove, cooler(3))
	if free < 1 || held > free {
		t.Errorf("vents %d without wall stations, %d with three coolers", free, held)
	}
}

// A vent that removes the cooler saves the cooler's steel (#2950): with the
// heaters and coolers priced, a room of heat-giving stations takes vents at a
// lower load than with only the vent's own build counted.
func TestTileVentsCountTheCoolerTheyRemove(t *testing.T) {
	plants := testClimate().Build
	plants.Heater, plants.Cooler = 50, 50
	mild := []float64{10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10}
	firstVented := func(build ClimateBuildValue) float64 {
		for heat := 10.0; heat <= 300; heat += 10 {
			res := ventTilingBuilt(t, mild, ventYes, HeatSource{HeatPerSecond: heat, MinC: -99999, MaxC: 99999}, 21, build)
			if len(ventsWhy(res, ReasonTileVent)) > 0 {
				return heat
			}
		}
		return 1e9
	}
	with, without := firstVented(plants), firstVented(testClimate().Build)
	if with >= 1e9 || !(with < without) {
		t.Errorf("vents first placed at %v heat/s with plants priced, %v without", with, without)
	}
}

func TestValidRejectsAVentNotInAWall(t *testing.T) {
	res := ventTiling(t, seaIce, ventYes, furnaceHeat)
	base := res.Plan
	if !base.Valid() || len(ventsOf(res)) == 0 {
		t.Fatal("no valid plan with a vent to start from")
	}
	in := base.Rooms[0].Region.Bounds()
	withVent := func(c domain.Cell) LayoutPlan {
		p := base
		p.Sites = append(slices.Clone(base.Sites), newSite("probe", NodeVent, RectRegion(Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}), ReasonTileVent))
		return p
	}
	for name, c := range map[string]domain.Cell{
		"floor":        {X: in.X + 1, Z: in.Z + 1},
		"corner":       {X: in.X - 1, Z: in.Z - 1},
		"far from any": {X: 5, Z: 5},
		"link door":    base.Links[0].Cell,
	} {
		if withVent(c).Valid() {
			t.Errorf("%s %v accepted as a vent", name, c)
		}
	}
}
