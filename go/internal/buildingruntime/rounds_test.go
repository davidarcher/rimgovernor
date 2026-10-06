package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"os"
	"slices"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type roundsNative struct {
	reply     *o.ColonyFactsReply
	onRead    func(context.Context)
	reads     int
	planning  bool
	pawnReply *o.ListPawnsReply
	// pawnReads and populationReads count the held-section reads (#625).
	pawnReads, populationReads int
	// identityTick, when set, pins the identity read behind the colony
	// reply's tick the way the step's fact cache serves it under a running
	// window (#662).
	identityTick *int64
	// built is the fake construction census (rounds_built_census_test.go).
	built map[domain.ActionID]*o.BuildingState
	// cells is the planning window the fake serves (ReadPlanningWindow);
	// nil serves an empty window.
	cells *bridge.PlanningWindow
	// catalog is the definition catalog's rows (#1340); finished, when
	// set, is the frame's finished research.
	catalog  []bridge.FixtureDef
	finished []string
	// recipes are the def mirror's RecipeDef rows.
	recipes []*d.RecipeDef
	// buildings, pawns and things are the frame's keyed tables (#1343).
	buildings bridge.Buildings
	pawns     bridge.Pawns
	things    bridge.Things
	// itemDefs are the defs whose catalog facts a test sets (thingCatalog).
	itemDefs map[string]itemDef
	// races are the animal races whose catalog rows a test sets
	// (thingCatalog), by def name.
	races map[string]*d.RaceProperties
	// mechs are the mechanoid races (thingCatalog), by def name.
	mechs map[string]bool
}

func (n *roundsNative) FrameTables(context.Context, *c.Identity) (bridge.Tables, error) {
	return bridge.Tables{Buildings: n.buildings, Pawns: n.pawns, Things: n.things, Catalog: n.thingCatalog()}, nil
}

// testDiningFurniture is what the fake catalog's dining rows derive to
// (diningFixtureDefs): its chair, table and the foothold pin with the
// six-cell lane of a watch stand distance of five.
var testDiningFurniture = policy.DiningFurniture{
	Chair: policy.InteriorPieceDef{Def: "DiningChair", Size: domain.Cell{X: 1, Z: 1}},
	Table: policy.InteriorPieceDef{Def: "Table1x2c", Size: domain.Cell{X: 1, Z: 2}},
	Pin:   policy.InteriorPieceDef{Def: "HorseshoesPin", Size: domain.Cell{X: 1, Z: 1}},
	Lane:  6,
}

// diningFixtureDefs are the fake catalog's chair and table rows beside its
// foothold pin: a sittable chair and a one-by-two eating surface.
func diningFixtureDefs() []bridge.FixtureDef {
	wood := []policy.Amount{{Resource: "WoodLog", Count: 20}}
	return []bridge.FixtureDef{
		{Name: "DiningChair", Sittable: true, Comfort: .7, Costs: wood},
		{Name: "Table1x2c", Width: 1, Height: 2, EatSurface: true, Costs: wood},
	}
}

// itemDef is what a test says about a def beyond the plain one: its base
// deterioration and whether the game calls it medicine.
type itemDef struct {
	deterioration float32
	medicine      bool
}

// thingCatalog is a decoded catalog with a plain def row for every def the
// frame's things and buildings tables hold (a corpse's source race is a
// humanlike "Human"), each with its game-computed flags and a base
// deterioration from n.itemDefs: the def rows and stat values a food stock
// and an upkeep item join to (#1733). A building def is a powered one too, so
// a power row of any building resolves.
func (n *roundsNative) thingCatalog() *bridge.DefinitionCatalog {
	id := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	v := &o.DefinitionCatalog{Context: &c.ObservationContext{Identity: id, Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)},
		TerrainDefs: []*d.TerrainDef{{DefName: "Soil"}},
		// The one joy building every fake colony can build: a watch-building
		// pin that draws no power and needs no research, the recreation
		// foothold.
		ClassChains: []*o.ClassChain{{Name: "RimWorld.JoyGiver_WatchBuilding", Bases: []string{"RimWorld.JoyGiver"}}, {Name: "RimWorld.CompProperties_Power"},
			{Name: "RimWorld.CompProperties_Battery", Bases: []string{"RimWorld.CompProperties_Power"}},
			{Name: "RimWorld.CompPowerPlant"}, {Name: "RimWorld.CompPowerPlantSolar", Bases: []string{"RimWorld.CompPowerPlant"}}, {Name: "RimWorld.CompPowerPlantWind", Bases: []string{"RimWorld.CompPowerPlant"}}},
		Defs: &d.DefSets{StatDefs: []*d.StatDef{{DefName: "MarketValue"}}, RoomStatDefs: bridge.FixtureRoomStats(),
			JobDefs:      []*d.JobDef{{DefName: "Play_Horseshoes", JoyGainRate: 1, JoyDuration: 1000}},
			JoyGiverDefs: []*d.JoyGiverDef{{DefName: "Play_Horseshoes", GiverClass: "RimWorld.JoyGiver_WatchBuilding", ThingDefs: []string{"HorseshoesPin"}, JobDef: "Play_Horseshoes"}}},
		StatValues: &o.DefStatTable{Stats: []string{bridge.StatDeteriorationRate}},
		Constants:  &o.CatalogConstants{TicksPerHour: 2500, TicksPerDay: 60000, DaysPerYear: 60, BillStackMax: 15, SkillMaxLevel: 20, LitGlowThreshold: 0.3, FullRotRateC: 10, RoofMaxSupportDistance: 6.9, CurrencyDef: "Silver", WortDef: "Wort"}}
	seen := map[string]bool{}
	add := func(row *d.ThingDef) {
		if row.DefName == "" || seen[row.DefName] {
			return
		}
		seen[row.DefName] = true
		item := n.itemDefs[row.DefName]
		v.ThingDefs = append(v.ThingDefs, row)
		v.ThingFacts = append(v.ThingFacts, &o.ThingDefFacts{DefName: row.DefName, Medicine: item.medicine})
		v.StatValues.Rows = append(v.StatValues.Rows, &o.DefStatRow{DefName: row.DefName, Stat: []int32{0}, Value: []float32{item.deterioration}})
	}
	add(&d.ThingDef{DefName: "HorseshoesPin", Building: &d.BuildingProperties{JoyKind: "Gaming_Dexterity"}})
	add(&d.ThingDef{DefName: "Silver"})
	// The power family's rows: each generator's power comp (negative draw, its
	// plant class) and the battery's storage.
	generator := func(name, class string, watts float32) {
		add(&d.ThingDef{DefName: name, Comps: []*d.Opt_CompPropertiesAny{{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Power{CompProperties_Power: &d.CompProperties_Power{CompClass: class, BasePowerConsumption: -watts}}}}}})
	}
	generator("SolarGenerator", "RimWorld.CompPowerPlantSolar", 1700)
	generator("WindTurbine", "RimWorld.CompPowerPlantWind", 2300)
	generator("WoodFiredGenerator", "RimWorld.CompPowerPlant", 1000)
	generator("ChemfuelPoweredGenerator", "RimWorld.CompPowerPlant", 1000)
	generator("GeothermalGenerator", "RimWorld.CompPowerPlant", 3600)
	add(&d.ThingDef{DefName: "Battery", Comps: []*d.Opt_CompPropertiesAny{{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Battery{CompProperties_Battery: &d.CompProperties_Battery{StoredEnergyMax: 600, Efficiency: 0.5}}}}}})
	add(&d.ThingDef{DefName: "Human", Race: &d.RaceProperties{Intelligence: d.Intelligence_INTELLIGENCE_HUMANLIKE}})
	for name, props := range n.races {
		add(&d.ThingDef{DefName: name, Race: props})
		for _, facts := range v.ThingFacts {
			if facts.DefName == name {
				facts.Race = &o.RaceFacts{Animal: true}
			}
		}
	}
	for name := range n.mechs {
		add(&d.ThingDef{DefName: name, Race: &d.RaceProperties{}})
		for _, facts := range v.ThingFacts {
			if facts.DefName == name {
				facts.Race = &o.RaceFacts{Mechanoid: true}
			}
		}
	}
	plain := func(name string) {
		// A def a test gave a wattage (PowerW) draws it, as its row states.
		var watts float32
		for _, def := range n.catalog {
			if def.Name == name && def.PowerW != nil {
				watts = float32(*def.PowerW)
			}
		}
		add(&d.ThingDef{DefName: name, Ingestible: &d.IngestibleProperties{SourceDef: "Human"},
			Comps: []*d.Opt_CompPropertiesAny{{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Power{CompProperties_Power: &d.CompProperties_Power{BasePowerConsumption: watts}}}}}})
	}
	for row := range n.things.Values() {
		plain(row.GetThing().GetDefName())
	}
	for row := range n.buildings.Values() {
		plain(row.GetBuilding().GetDefName())
	}
	bridge.FixtureEnvironmentDefs(v)
	catalog, err := bridge.DecodeDefinitionCatalog(v, id)
	if err != nil {
		panic(err)
	}
	return catalog
}

func (n *roundsNative) FrameThings(context.Context, *c.Identity) (bridge.Things, error) {
	return n.things, nil
}

// thing puts row in the frame's things table and returns the reference a
// food stock carries to it.
func (n *roundsNative) thing(row *o.Thing) *c.Ref {
	n.things = n.things.With(row.Thing.GetId(), row)
	return &c.Ref{Id: row.Thing.Id}
}

// entity puts a things table row with only ref's head in the frame,
// unless the frame already holds one, and returns the reference a section
// carries to it (#1342).
func (n *roundsNative) entity(ref *o.EntityRef) *c.Ref {
	if _, ok := n.things.Get(ref.GetId()); !ok {
		n.thing(&o.Thing{Thing: ref})
	}
	return &c.Ref{Id: ref.Id}
}

// building puts row in the frame's building table and returns the
// reference a section carries to it; a row without service or settings
// gets empty ones.
func (n *roundsNative) building(row *o.BuildingState) *o.EntityRef {
	if row.Service == nil {
		row.Service = &o.BuildingServiceState{}
	}
	if row.Settings == nil {
		row.Settings = &o.BuildingSettings{Forbidden: proto.Bool(false)}
	}
	n.buildings = n.buildings.With(row.Building.GetId(), row)
	return row.Building
}

// head puts a building row with only ref's head in the frame's building
// table, unless it already holds one, and returns the reference a section
// carries to it (#1342).
func (n *roundsNative) head(ref *o.EntityRef) *c.Ref {
	if _, ok := n.buildings.Get(ref.GetId()); !ok {
		n.building(&o.BuildingState{Building: ref})
	}
	return &c.Ref{Id: ref.Id}
}

// pawn puts row in the frame's pawn table and returns the reference a
// section carries to it.
func (n *roundsNative) pawn(row *o.PawnState) *o.EntityRef {
	n.pawns = n.pawns.With(row.Pawn.GetId(), row)
	return &o.EntityRef{Id: row.Pawn.Id}
}

func (n *roundsNative) finishedResearch() []string { return n.finished }

// DefinitionCatalog serves the fake's catalog rows under the asked load.
func (n *roundsNative) DefinitionCatalog(_ context.Context, id *c.Identity) (*bridge.DefinitionCatalog, error) {
	// The fake's rows beside the one joy building every fake colony can
	// build: a watch-building pin that draws no power and needs no research,
	// the recreation foothold.
	foothold := bridge.FixtureDef{Name: "HorseshoesPin", Joy: &bridge.FixtureJoy{Kind: "Gaming_Dexterity", WatchGiver: true}}
	// The garments a gear census's loadout model may name, with the stat rows
	// the model joins to.
	skin := &bridge.FixtureApparel{Layers: []string{"OnSkin"}, Groups: []string{"Torso", "Arms"}, Tags: []string{"Worker", "Soldier"}, Sharp: .05}
	cloth := []bridge.FixtureStuff{{Stuff: "Cloth"}}
	garments := []bridge.FixtureDef{
		{Name: "Apparel_BasicShirt", Apparel: skin, Stuffs: cloth},
		{Name: "Apparel_Parka", Apparel: skin, Stuffs: cloth},
		{Name: "Apparel_PowerArmor", Apparel: &bridge.FixtureApparel{Layers: []string{"Middle", "Shell"}, Groups: []string{"Torso", "Neck", "Shoulders", "Arms", "Legs"}, Tags: []string{"Soldier"}, Sharp: 1.2, Blunt: .5}},
		{Name: "Apparel_ArmorRecon", Apparel: &bridge.FixtureApparel{Layers: []string{"Middle"}, Groups: []string{"Torso", "Neck"}, Tags: []string{"Soldier"}, Sharp: .9, Blunt: .3}},
		{Name: "Apparel_FlakVest", Apparel: &bridge.FixtureApparel{Layers: []string{"Middle"}, Groups: []string{"Torso", "Neck"}, Tags: []string{"Soldier"}, Sharp: 1, Blunt: .36, Market: 223}},
	}
	named := map[string]bool{}
	for _, def := range n.catalog {
		named[def.Name] = true
	}
	defs := append([]bridge.FixtureDef{foothold}, diningFixtureDefs()...)
	for _, g := range garments {
		if !named[g.Name] {
			defs = append(defs, g)
		}
	}
	// The Core weapons every weapon score reads its rows from (#1723).
	for _, w := range bridge.CoreWeaponFixtures() {
		if !named[w.Name] {
			defs = append(defs, w)
		}
	}
	catalog := testCatalog(id, bridge.WithCoreFurniture(append(defs, n.catalog...))...)
	shirt := &d.ApparelProperties{BodyPartGroups: []string{"Torso", "Arms"}, Layers: []string{"OnSkin"}, DevelopmentalStageFilter: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT}
	for _, row := range []*d.ThingDef{{DefName: "Apparel_BasicShirt", Apparel: shirt}, {DefName: "Apparel_Parka", Apparel: shirt}, {DefName: "Bow_Short"}, {DefName: "WoodLog"}} {
		if catalog.ThingDefs[row.DefName] == nil {
			catalog.ThingDefs[row.DefName] = row
		}
	}
	recorded, err := fullCatalogRows()
	if err != nil {
		return nil, err
	}
	for class, rows := range recorded {
		catalog.Defs[class] = rows
	}
	if len(n.recipes) > 0 {
		rows := map[string]proto.Message{}
		for _, r := range n.recipes {
			rows[r.DefName] = r
		}
		catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] = rows
	}
	return catalog, nil
}

// testCatalog is a definition catalog of fixture defs under id's load.
func testCatalog(id *c.Identity, rows ...bridge.FixtureDef) *bridge.DefinitionCatalog {
	return bridge.FixtureCatalog(id.GetLoadToken(), rows...).FixtureItemFacts(policy.CoreItemFacts())
}

// madeOf is the stuff options of a def built from one stuff.
func madeOf(stuff string) []observation.StuffOption {
	return []observation.StuffOption{{Stuff: stuff}}
}

// testPieceShapes are the piece shapes of the Core furniture rows, which the
// room observations of these tests carry as the projection would.
var testPieceShapes = func() policy.PieceShapes {
	shapes, err := bridge.FixtureCatalog("load", bridge.CoreFurnitureFixtures()...).PieceShapes()
	if err != nil {
		panic(err)
	}
	return shapes
}()

// buildable is a plain buildable fixture def of the given construction skill
// and size.
func buildable(name string, skill, width, height int32) bridge.FixtureDef {
	return bridge.FixtureDef{Name: name, ConstructionSkill: skill, Width: width, Height: height}
}

// putCatalog replaces or appends catalog rows by name.
func (n *roundsNative) putCatalog(rows ...bridge.FixtureDef) {
	for _, row := range rows {
		*n.catalogRow(row.Name) = row
	}
}

// catalogRow is a catalog row by name, appended when absent; the pointer is
// good until the next append.
func (n *roundsNative) catalogRow(name string) *bridge.FixtureDef {
	for i := range n.catalog {
		if n.catalog[i].Name == name {
			return &n.catalog[i]
		}
	}
	n.catalog = append(n.catalog, bridge.FixtureDef{Name: name})
	return &n.catalog[len(n.catalog)-1]
}

// ReadPlanningWindow serves the fake's planning window whatever region is
// asked, as the frame grid answers it.
func (n *roundsNative) ReadPlanningWindow(ctx context.Context, _ *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	if n.cells == nil {
		return bridge.PlanningWindow{Context: n.reply.GetObserved().GetContext(), Region: rect}, bridge.Result{}, ctx.Err()
	}
	window := *n.cells
	window.Cells = slices.Clone(n.cells.Cells)
	if window.Region == (policy.Rectangle{}) {
		window.Region = rect
	}
	return window, bridge.Result{}, ctx.Err()
}

// openCell is a visible, walkable, unroofed, unzoned outdoor cell with
// light footing and no edifice, as the frame grid reads one.
func openCell(x, z int32) policy.SiteCell {
	return policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), SupportsLight: domain.Known(true), Indoors: domain.Known(false),
		Zone: domain.Known(false), Roofed: domain.Known(false), PlayerEdifice: domain.Known(""), ClaimableRuin: domain.Known("")}
}

// roofed is c under a constructed roof.
func roofed(c policy.SiteCell) policy.SiteCell {
	c.Roofed, c.Roof = domain.Known(true), domain.Known("RoofConstructed")
	return c
}

// fixtureCells is the colony-core fixture's planning window: its one
// fertile cell at the origin.
func fixtureCells(t testing.TB) *bridge.PlanningWindow {
	t.Helper()
	cell := openCell(0, 0)
	cell.Indoors, cell.Fertility = domain.Unknown[bool](), domain.Known(1.0)
	return &bridge.PlanningWindow{Context: &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), MapId: proto.Int32(0), LoadToken: proto.String("load")}, Tick: proto.Int64(7), NativeGeneration: proto.Uint64(1)},
		Region: policy.Rectangle{Width: 1, Height: 1}, Cells: []policy.SiteCell{cell}}
}

// zonesAvailable drops the fixture's farms issue so the zone census reads.
func zonesAvailable(v *o.ColonyFactsSnapshot) {
	var issues []*o.ReadIssue
	for _, i := range v.Issues {
		if i.GetField() != "farms" {
			issues = append(issues, i)
		}
	}
	v.Issues = issues
}

// Translate the legacy colony fixture's zone data at the new list boundary.
func (n *roundsNative) ReadZoneSection(ctx context.Context, _ *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	v := n.reply.GetObserved()
	out := bridge.ZonesRead{Context: v.Context, AsOf: v.Context.GetTick()}
	for _, issue := range v.Issues {
		if issue.GetField() == "farms" {
			return out, bridge.Result{}, bridge.ErrUnavailable
		}
	}
	for _, farm := range v.Farms {
		out.Rows = append(out.Rows, &o.ZoneState{Id: farm.Zone.Id, Farm: farm, FoodStorage: proto.Bool(false)})
	}
	if v.GetFoodStorage() {
		out.Rows = append(out.Rows, &o.ZoneState{Id: proto.String("storage"), FoodStorage: proto.Bool(true)})
	}
	return out, bridge.Result{}, ctx.Err()
}

func (n *roundsNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}

// fakeFrame is the frame a test fake serves: the colony reply's context
// and whichever section reads the fake (source, the outermost type, so its
// overrides count) offers.
func fakeFrame(ctx context.Context, source observation.ColonySource, id *c.Identity) (bridge.RoundsFrame, error) {
	colony, _, err := source.ReadColonyFacts(ctx, id, true)
	if err != nil {
		return bridge.RoundsFrame{}, err
	}
	frame := bridge.RoundsFrame{Context: colony.GetObserved().GetContext(), Colony: colony.GetObserved()}
	if frame.Tables, err = source.FrameTables(ctx, id); err != nil {
		return bridge.RoundsFrame{}, err
	}
	if s, ok := source.(interface {
		DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error)
	}); ok {
		if frame.Catalog, err = s.DefinitionCatalog(ctx, id); err != nil {
			return bridge.RoundsFrame{}, err
		}
	}
	if s, ok := source.(interface {
		ReadTemperatureRooms(context.Context, *c.Identity) (*o.ListRoomsReply, bridge.Result, error)
	}); ok {
		rooms, _, err := s.ReadTemperatureRooms(ctx, id)
		if err != nil && !errors.Is(err, bridge.ErrUnavailable) {
			return bridge.RoundsFrame{}, err
		}
		if err == nil {
			frame.Rooms = rooms.GetObserved()
			frame.RoomCells = extentCells(frame.Rooms)
		}
	}
	if s, ok := source.(interface {
		ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	}); ok {
		if frame.Emergency, _, err = s.ReadEmergency(ctx, id); err != nil {
			return bridge.RoundsFrame{}, err
		}
	}
	var ids []string
	if complete, known := frame.Emergency.Facts.ColonistsComplete.Value(); known && complete {
		for _, pawn := range frame.Emergency.Facts.Colonists {
			ids = append(ids, string(pawn.ID))
		}
	}
	if s, ok := source.(interface {
		ReadRoundsPawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
	}); ok && len(ids) > 0 {
		reply, _, err := s.ReadRoundsPawns(ctx, id, ids)
		if err != nil {
			return bridge.RoundsFrame{}, err
		}
		frame.Pawns = reply.GetObserved()
	}
	if s, ok := source.(interface {
		ReadRoundsPopulation(context.Context, *c.Identity) (bridge.PrisonerCensus, bridge.Result, error)
	}); ok {
		population, _, err := s.ReadRoundsPopulation(ctx, id)
		if err != nil {
			return bridge.RoundsFrame{}, err
		}
		frame.Population = &population
	}
	if s, ok := source.(RoundsResearchSource); ok {
		research, _, err := s.ReadResearch(ctx, id)
		if err != nil {
			return bridge.RoundsFrame{}, err
		}
		frame.Research = &research
	}
	if s, ok := source.(interface{ finishedResearch() []string }); ok && frame.Research == nil && s.finishedResearch() != nil {
		frame.Research = &bridge.ResearchRead{Context: frame.Context, Finished: s.finishedResearch()}
	}
	if s, ok := source.(interface {
		ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error)
	}); ok {
		quests, _, err := s.ReadWorldProgression(ctx, id, false)
		if err != nil {
			return bridge.RoundsFrame{}, err
		}
		frame.Quests = &quests
	}
	if s, ok := source.(interface {
		ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error)
	}); ok {
		traders, _, err := s.ListTraders(ctx, id)
		if err != nil {
			return bridge.RoundsFrame{}, err
		}
		frame.Traders = &traders
	}
	if s, ok := source.(interface {
		ReadConstructionBuildings(context.Context, *c.Identity, []string) (*o.ListBuildingsReply, bridge.Result, error)
	}); ok {
		reply, _, err := s.ReadConstructionBuildings(ctx, id, nil)
		if err != nil && reply.GetUnavailable() == nil {
			return bridge.RoundsFrame{}, err
		}
		frame.Buildings = bridge.BuildingCensusOf(reply.GetObserved())
	}
	if s, ok := source.(observation.ZonesNative); ok {
		zones, _, err := s.ReadZoneSection(ctx, id)
		if err != nil && !errors.Is(err, bridge.ErrUnavailable) {
			return bridge.RoundsFrame{}, err
		}
		if err == nil {
			frame.Zones = &zones
		}
	}
	return frame, nil
}

func (n *roundsNative) ReadRoundsPawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	n.pawnReads++
	if n.pawnReply != nil {
		return n.pawnReply, bridge.Result{}, ctx.Err()
	}
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}, bridge.Result{}, ctx.Err()
}

func (n *roundsNative) ReadRoundsPopulation(ctx context.Context, _ *c.Identity) (bridge.PrisonerCensus, bridge.Result, error) {
	n.populationReads++
	return bridge.PrisonerCensus{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Prisoners: domain.Known([]policy.PrisonerFacts{})}, bridge.Result{}, ctx.Err()
}

func (n *roundsNative) ReadEmergency(ctx context.Context, _ *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}}, bridge.Result{}, ctx.Err()
}

func (n *roundsNative) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	observed := proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext)
	if n.identityTick != nil {
		observed.Tick = proto.Int64(*n.identityTick)
	}
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: observed, Paused: proto.Bool(true)}}}, bridge.Result{}, ctx.Err()
}
func (n *roundsNative) ReadColonyFacts(ctx context.Context, _ *c.Identity, planning bool) (*o.ColonyFactsReply, bridge.Result, error) {
	n.reads++
	n.planning = planning
	if n.onRead != nil {
		n.onRead(ctx)
	}
	return n.reply, bridge.Result{}, nil // A late transport may ignore cancellation.
}

func TestRounderUsesConfiguredFieldReserve(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, _, _, n := roundsFixture(t)
	v := n.reply.GetObserved()
	v.Issues = v.Issues[1:] // Complete native farm census replaces its unavailable issue.
	v.Farms = []*o.FarmFacts{{Zone: &c.Ref{Id: proto.String("farm")}, Crop: proto.String("Plant_Rice"), EdibleCrop: proto.Bool(true), PlantedCells: proto.Uint32(73), FertilePlantedCells: proto.Uint32(73), UsableCells: proto.Uint32(73),
		Temperature: proto.Float64(20), MinGrowthTemperature: proto.Float64(0), MinOptimalGrowthTemperature: proto.Float64(10), MaxOptimalGrowthTemperature: proto.Float64(30), MaxGrowthTemperature: proto.Float64(42)}}
	n.catalog[0] = bridge.FixtureDef{Name: "Plant_Rice", Plant: &bridge.FixturePlant{GrowDays: 3, HarvestNutrition: 1}}
	for _, reserve := range []float64{7, 14, 7} {
		r.policy.FoodTargetDays = reserve
		out, err := r.Step(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		if !n.planning {
			t.Fatal("routine did not request crop definitions")
		}
		want := domain.FindingUnclear // Stored-food forecast remains unknown.
		if reserve == 14 {
			want = domain.FindingUnmet
		}
		for _, binding := range out.Review.Standards {
			if binding.Concern != policy.EnsureFoodSupply {
				continue
			}
			g, err := db.LoadStandard(context.Background(), binding.Standard)
			if err != nil || g.Standard.Finding != want {
				t.Fatal("field budget did not reach durable food need", reserve, g, err)
			}
		}
	}
}
func roundsFixture(t *testing.T) (*Rounder, *store.Store, *playerFakeSession, store.ControlRequest, *roundsNative) {
	t.Helper()
	p, db, session, _ := playerFixture(t)
	request := playerAcquire(t, p)
	if _, err := p.Resume(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	n := colonyCoreNative(t)
	r, err := NewRounder(p, n, testkit.NewManualClock(time.Now()), policy.DefaultRoundsPolicy(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if centre := n.reply.GetObserved().GetCenter(); centre != nil {
		centreOn(r, domain.Cell{X: centre.GetX(), Z: centre.GetZ()})
	}
	return r, db, session, request, n
}

// colonyCoreNative is the reviewer's fact source over the committed
// colony-core fixture: world colony/load/map 0 at tick 7, generation 1.
func colonyCoreNative(t *testing.T) *roundsNative {
	t.Helper()
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	n := &roundsNative{reply: &o.ColonyFactsReply{}}
	if err = protojson.Unmarshal(data, n.reply); err != nil {
		t.Fatal(err)
	}
	n.cells = fixtureCells(t)
	n.catalog = []bridge.FixtureDef{{Name: "Wall", Stuffs: []bridge.FixtureStuff{{Stuff: "WoodLog", Costs: []policy.Amount{{Resource: "WoodLog", Count: 5}}}}}}
	return n
}

// reviewedConcernCount is the review's concern total, derived from the policy
// inspection catalog: every Standard and Project concern, no Incident (the
// rounds open incidents and file no row for them).
func reviewedConcernCount() int {
	n := 0
	for _, d := range policy.AllInspections() {
		if !policy.IsIncidentKind(d.Concern) {
			n++
		}
	}
	return n
}

func TestRounderPersistsNeedsAndManualVetoesWithoutRead(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, session, request, n := roundsFixture(t)
	got, err := r.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	// The review yields every catalogued Standard and Project concern.
	if len(got.Standards)+len(got.Projects) != reviewedConcernCount() || got.Review.Revision != 1 || !got.Review.Enabled {
		t.Fatal(got)
	}
	for _, binding := range got.Review.Standards {
		if binding.Concern == domain.ConcernID(policy.EnsureFoodSupply) {
			g, err := db.LoadStandard(context.Background(), binding.Standard)
			if err != nil || g.Standard.Finding != domain.FindingUnclear {
				t.Fatal("raw food became recovery", g, err)
			}
		}
	}
	request.Kind = store.PauseControl
	request.RequestID = "manual-routine"
	request.World.Load = "stale-browser"
	if _, err = r.player.Pause(context.Background(), request); err == nil {
		t.Fatal("stale browser accepted")
	}
	stored, err := db.LoadRounds(context.Background())
	if err != nil || stored.Enabled || stored.Revision != 2 || n.reads != 1 || session.State().Enabled {
		t.Fatal(stored, err)
	}
	for _, binding := range stored.Standards {
		g, err := db.LoadStandard(context.Background(), binding.Standard)
		if err != nil || g.Standard.Status == domain.StandardVoided || stored.Veto(g.Standard) != "control paused" {
			t.Fatal(g, err)
		}
	}
}

func TestRounderRejectsAuthorityChangesDuringRead(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	for _, change := range []string{"direction", "disabled", "native", "load"} {
		t.Run(change, func(t *testing.T) {
			r, db, session, _, n := roundsFixture(t)
			n.onRead = func(context.Context) {
				session.mu.Lock()
				defer session.mu.Unlock()
				switch change {
				case "direction":
					session.state.Snapshot.Native++
				case "disabled":
					session.state.Enabled = false
				case "native":
					session.state.Snapshot.Native++
				case "load":
					session.state.Snapshot.Load = "new"
				}
			}
			if _, err := r.Step(context.Background()); err == nil {
				t.Fatal("late facts committed")
			}
			stored, err := db.LoadRounds(context.Background())
			if err != nil || stored.Revision != 0 {
				t.Fatal(stored, err)
			}
		})
	}
}

func TestRounderManualCancelsBlockedNativeRead(t *testing.T) {
	t.Parallel()
	r, db, session, request, n := roundsFixture(t)
	entered := make(chan struct{})
	n.onRead = func(ctx context.Context) { close(entered); <-ctx.Done() }
	reviewDone := make(chan error, 1)
	go func() { _, err := r.Step(context.Background()); reviewDone <- err }()
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("read not entered")
	}
	request.Kind, request.RequestID = store.PauseControl, "manual-blocked"
	if _, err := r.player.Pause(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	if err := <-reviewDone; err == nil {
		t.Fatal("cancelled review succeeded")
	}
	stored, err := db.LoadRounds(context.Background())
	if err != nil || stored.Revision != 0 || session.State().Enabled {
		t.Fatal(stored, err)
	}
}

func TestRounderDisabledStepRetiresReviewWithoutReacquiring(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, session, _, n := roundsFixture(t)
	if _, err := r.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := session.Disable(); err != nil {
		t.Fatal(err)
	}
	got, err := r.Step(context.Background())
	if err != nil || got.Review.Enabled || got.Review.Revision != 2 {
		t.Fatal(got, err)
	}
	got, err = r.Step(context.Background())
	if err != nil || got.Review.Revision != 2 || n.reads != 1 || session.acquires.Load() != 1 {
		t.Fatal(got, err)
	}
	stored, err := db.LoadRounds(context.Background())
	if err != nil || stored.Enabled {
		t.Fatal(stored, err)
	}
}

type roundsMedicalNative struct {
	*roundsNative
	unknown bool
}

func (n *roundsMedicalNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.roundsNative.ReadEmergency(ctx, id)
	pawn := policy.EmergencyPawn{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(true)}
	if n.unknown {
		pawn.NeedsTend = domain.Unknown[bool]()
	}
	v.Facts.Colonists = []policy.EmergencyPawn{pawn}
	return v, receipt, err
}
func TestRounderUsesSameTickMedicalCensus(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"needs_tend", "unknown"} {
		t.Run(kind, func(t *testing.T) {
			r, db, _, _, n := roundsFixture(t)
			r.native = &roundsMedicalNative{roundsNative: n, unknown: kind == "unknown"}
			got, err := r.Step(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			// A deficit opens the CriticalMedical incident (#1020); an
			// unknown census opens none.
			binding, ok := got.Review.Incident(policy.CriticalMedicine)
			if kind == "unknown" {
				if ok {
					t.Fatal("unknown census opened an incident", binding)
				}
				return
			}
			if !ok || binding.Situation != domain.SituationActive {
				t.Fatal("medical incident missing", got.Review.Incidents)
			}
			if _, err := db.LoadIncident(context.Background(), binding.Incident); err != nil {
				t.Fatal(err)
			}
		})
	}
}

func TestRoundsFoodAttrsCarryRunwayThresholdsAndCalendar(t *testing.T) {
	calendar := policy.Calendar{Season: "Fall", DayOfYear: 33, GrowingDays: 30, GrowingDaysRemaining: 1, NonGrowingDays: 33, Sowing: true}
	facts := policy.RoundsFacts{FoodDays: domain.Known(38.5), Calendar: domain.Known(calendar)}
	seasonal := policy.DefaultRoundsPolicy().Seasonal(facts.Calendar, facts.DisasterConditions)
	attrs := roundsFoodAttrs(facts, seasonal)
	got := map[string]any{}
	for i := 0; i+1 < len(attrs); i += 2 {
		got[attrs[i].(string)] = attrs[i+1]
	}
	if got["food_days"] != 38.5 || got["food_min_days"] != seasonal.FoodMinDays || got["food_target_days"] != seasonal.FoodTargetDays {
		t.Fatalf("food attrs: %v", got)
	}
	if got["season"] != "Fall" || got["day_of_year"] != int64(33) || got["growing_days_remaining"] != 1.0 || got["growing_days_until"] != 0.0 || got["non_growing_days"] != 33.0 {
		t.Fatalf("calendar attrs: %v", got)
	}
	if seasonal.FoodMinDays <= policy.DefaultRoundsPolicy().FoodMinDays {
		t.Fatalf("seasonal minimum not widened: %v", seasonal.FoodMinDays)
	}
	unknown := roundsFoodAttrs(policy.RoundsFacts{}, policy.DefaultRoundsPolicy())
	for i := 0; i < len(unknown); i += 2 {
		if key := unknown[i].(string); key == "food_days" || key == "season" {
			t.Fatalf("unknown facts produced %s", key)
		}
	}
}

func (n *roundsMedicalNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}

// extentCells stands in for the frame grid: each test room fills its
// extents.
func extentCells(v *o.RoomsSnapshot) map[string][]domain.Cell {
	out := map[string][]domain.Cell{}
	for _, room := range v.GetRooms() {
		lo, hi := room.GetExtents().GetMinimum(), room.GetExtents().GetMaximum()
		if lo == nil || hi == nil {
			continue
		}
		for z := lo.GetZ(); z <= hi.GetZ(); z++ {
			for x := lo.GetX(); x <= hi.GetX(); x++ {
				out[room.GetId()] = append(out[room.GetId()], domain.Cell{X: x, Z: z})
			}
		}
	}
	return out
}

func (n *roundsNative) Tick(ctx context.Context) (*l.TickReply, bridge.Result, error) {
	reply, raw, err := n.Identity(ctx)
	if err != nil {
		return nil, raw, err
	}
	return &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: reply.GetLoaded().Context, Paused: reply.GetLoaded().Paused}}}, raw, nil
}
