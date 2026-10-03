package bridge

import (
	"cmp"
	"fmt"
	"maps"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// FixtureDef states one buildable or sowable def of a test catalog (#1731):
// the facts a planner's test cares about, written the way the game's rows
// carry them, so that FixtureCatalog can lay them out as the def rows, stat
// table and thing facts the planning views read.
type FixtureDef struct {
	Name string
	// Terrain makes the def a TerrainDef (buildable); the floor stats apply.
	Terrain  bool
	Research []string
	// ConstructionSkill is the construction skill the def requires.
	ConstructionSkill int32
	// Width and Height are the def's size; a zero size is 1x1.
	Width, Height int32
	// Costs is the adjusted cost list of a def not made from stuff.
	Costs []policy.Amount
	// Stuffs are the allowed stuffs of a def made from stuff, with the cost
	// list and the stat values (by StatDef name) of the def built from each.
	Stuffs []FixtureStuff
	// Stuffed makes a def made from stuff even with no allowed stuff.
	Stuffed bool
	// PowerW, GlowRadius and ExplosiveRadius add the comp carrying them.
	PowerW, GlowRadius, ExplosiveRadius *float64
	// MechCharger gives the def a thing class derived from Building_MechCharger.
	MechCharger bool
	// SowTag and GrowerFertility make the def a plant grower.
	SowTag          string
	GrowerFertility *float64
	// RoomRoles are the roles the game names for the def.
	RoomRoles []string
	// Plant makes the def a plant.
	Plant *FixturePlant
	// Floor are the stat values and path cost of a terrain.
	Floor FixtureFloor
	// WorkToBuild is the WorkToBuild stat of a def not made from stuff.
	WorkToBuild *float32
	// Joy makes the def a joy building offered by a joy giver.
	Joy *FixtureJoy
	// Apparel makes the def a garment of an adult wearer.
	Apparel *FixtureApparel
}

// FixtureApparel is a garment's layers, covered body part groups, outfit tags
// and armor ratings (every stuff of the def shares them).
type FixtureApparel struct {
	Layers, Groups, Tags []string
	Sharp, Blunt         float32
}

// FixtureJoy is a joy building's kind and the giver and job that offer it
// (one joy a session of 1 gain over 1000 ticks).
type FixtureJoy struct {
	Kind string
	// WatchGiver makes the giver a watch-building one.
	WatchGiver bool
}

// FixtureStuff is one allowed stuff of a stuffed fixture def.
type FixtureStuff struct {
	Stuff string
	Costs []policy.Amount
	Stats map[string]float32
}

// FixturePlant is a fixture crop; its product is a def named Name+"_Product".
type FixturePlant struct {
	GrowDays, FertilityMin, FertilitySensitivity, GrowMinGlow, HarvestWork float32
	// HarvestNutrition is the nutrition of one harvest (yield 1 of a product
	// worth that much).
	HarvestNutrition                 float32
	SowTags                          []string
	RawPreferred, Edible             bool
	RequiresPollution, RequiresClean bool
}

// FixtureFloor is a terrain's stat values.
type FixtureFloor struct {
	Cleanliness, Beauty, Flammability float32
	PathCost                          int32
}

// The classes a fixture def's rows name.
const (
	fixtureThingClass   = "Verse.ThingWithComps"
	fixtureChargerClass = "Test.Building_TestCharger"
)

// FixtureCatalog is a decoded definition catalog laid out from fixture defs,
// for a test that stands in for a loaded game. It decodes through the same
// path a recorded catalog does, so a malformed fixture panics.
func FixtureCatalog(loadToken string, defs ...FixtureDef) *DefinitionCatalog {
	wire := fixtureWire(defs)
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String(loadToken), MapId: proto.Int32(0)}
	wire.Context = &c.ObservationContext{Identity: identity, Tick: proto.Int64(12), NativeGeneration: proto.Uint64(7)}
	catalog, err := DecodeDefinitionCatalog(wire, identity)
	if err != nil {
		panic(fmt.Sprintf("bridge.FixtureCatalog: %v", err))
	}
	return catalog
}

func fixtureWire(defs []FixtureDef) *o.DefinitionCatalog {
	stats := []string{StatMaxHitPoints, StatFlammability, StatBedRestEffectiveness, StatWorkToBuild, StatMarketValue, StatNutrition, StatCleanliness, StatBeauty, "ArmorRating_Sharp", "ArmorRating_Blunt", "Insulation_Cold", "Insulation_Heat"}
	index := func(name string) int32 { return int32(slices.Index(stats, name)) }
	wire := &o.DefinitionCatalog{StatValues: &o.DefStatTable{Stats: stats}}
	wire.Defs = &d.DefSets{StatDefs: []*d.StatDef{{DefName: StatMarketValue}}, RoomStatDefs: FixtureRoomStats()}
	wire.Constants = &o.CatalogConstants{TicksPerHour: 2500, TicksPerDay: 60000, DaysPerYear: 60, BillStackMax: 15, SkillMaxLevel: 20, LitGlowThreshold: 0.3, FullRotRateC: 10, RoofMaxSupportDistance: 6.9, CurrencyDef: "Silver"}
	wire.TerrainDefs = []*d.TerrainDef{{DefName: "AnchorTerrain"}}
	wire.StatValues.TerrainRows = []*o.DefStatRow{{DefName: "AnchorTerrain", Stat: []int32{index(StatCleanliness), index(StatBeauty), index(StatFlammability)}, Value: []float32{0, 0, 0}}}
	chains := map[string][]string{fixtureThingClass: nil, fixtureChargerClass: {"RimWorld.Building_MechCharger"}, "RimWorld.Building_MechCharger": nil}
	for _, message := range []proto.Message{&d.CompProperties_Power{}, &d.CompProperties_Glower{}, &d.CompProperties_Explosive{}} {
		class, _ := proto.GetExtension(message.ProtoReflect().Descriptor().Options(), d.E_ClrType).(string)
		chains[class] = nil
	}
	items := map[string]bool{"Silver": true}
	row := func(def, stuff string, values map[int32]float32, costs []policy.Amount) {
		r := &o.DefStatRow{DefName: def, StuffName: stuff}
		for stat, value := range values {
			r.Stat = append(r.Stat, stat)
			r.Value = append(r.Value, value)
		}
		for _, cost := range costs {
			r.Costs = append(r.Costs, &o.Quantity{DefName: proto.String(string(cost.Resource)), Units: proto.Int64(cost.Count)})
			items[string(cost.Resource)] = true
		}
		wire.StatValues.Rows = append(wire.StatValues.Rows, r)
	}
	named := map[string]bool{}
	for _, def := range defs {
		named[def.Name] = true
	}
	for _, def := range defs {
		if def.Terrain {
			t := &d.TerrainDef{DefName: def.Name, DesignationCategory: "Floors", PathCost: def.Floor.PathCost, ResearchPrerequisites: def.Research, ConstructionSkillPrerequisite: def.ConstructionSkill}
			wire.TerrainDefs = append(wire.TerrainDefs, t)
			r := &o.DefStatRow{DefName: def.Name, Stat: []int32{index(StatCleanliness), index(StatBeauty), index(StatFlammability), index(StatWorkToBuild)}, Value: []float32{def.Floor.Cleanliness, def.Floor.Beauty, def.Floor.Flammability, fixtureWork(def.WorkToBuild)}}
			for _, cost := range def.Costs {
				r.Costs = append(r.Costs, &o.Quantity{DefName: proto.String(string(cost.Resource)), Units: proto.Int64(cost.Count)})
				items[string(cost.Resource)] = true
			}
			wire.StatValues.TerrainRows = append(wire.StatValues.TerrainRows, r)
			continue
		}
		width, height := def.Width, def.Height
		if width == 0 || height == 0 {
			width, height = 1, 1
		}
		t := &d.ThingDef{DefName: def.Name, DesignationCategory: "Misc", ThingClass: fixtureThingClass, ResearchPrerequisites: def.Research, ConstructionSkillPrerequisite: def.ConstructionSkill, Size: &d.IntVec2{X: width, Z: height}, Fertility: -1}
		if def.MechCharger {
			t.ThingClass = fixtureChargerClass
		}
		if def.Plant != nil {
			t.DesignationCategory = ""
		}
		apparelStats := map[int32]float32{}
		if a := def.Apparel; a != nil {
			t.Apparel = &d.ApparelProperties{Layers: a.Layers, BodyPartGroups: a.Groups, DefaultOutfitTags: a.Tags, DevelopmentalStageFilter: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT}
			apparelStats = map[int32]float32{index("ArmorRating_Sharp"): a.Sharp, index("ArmorRating_Blunt"): a.Blunt, index("Insulation_Cold"): 0, index("Insulation_Heat"): 0, index(StatMarketValue): 100}
		}
		if def.SowTag != "" {
			t.Building = &d.BuildingProperties{SowTag: def.SowTag}
			if def.GrowerFertility != nil {
				t.Fertility = float32(*def.GrowerFertility)
			}
		}
		if def.Joy != nil {
			t.Building = &d.BuildingProperties{JoyKind: def.Joy.Kind}
			t.DesignationCategory = "Joy"
			class := "RimWorld.JoyGiver_Other"
			if def.Joy.WatchGiver {
				class = watchGiverClass
			}
			chains[class] = []string{"RimWorld.JoyGiver"}
			chains["RimWorld.JoyGiver"] = nil
			job := "Play_" + def.Name
			wire.Defs.JobDefs = append(wire.Defs.JobDefs, &d.JobDef{DefName: job, JoyGainRate: 1, JoyDuration: 1000})
			wire.Defs.JoyGiverDefs = append(wire.Defs.JoyGiverDefs, &d.JoyGiverDef{DefName: job, GiverClass: class, ThingDefs: []string{def.Name}, JobDef: job})
		}
		comp := func(value *d.CompPropertiesAny) { t.Comps = append(t.Comps, &d.Opt_CompPropertiesAny{Value: value}) }
		if def.PowerW != nil {
			comp(&d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Power{CompProperties_Power: &d.CompProperties_Power{BasePowerConsumption: float32(*def.PowerW)}}})
		}
		if def.GlowRadius != nil {
			comp(&d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Glower{CompProperties_Glower: &d.CompProperties_Glower{GlowRadius: float32(*def.GlowRadius)}}})
		}
		if def.ExplosiveRadius != nil {
			comp(&d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Explosive{CompProperties_Explosive: &d.CompProperties_Explosive{ExplosiveRadius: float32(*def.ExplosiveRadius)}}})
		}
		facts := &o.ThingDefFacts{DefName: def.Name, RoomRoles: def.RoomRoles}
		if def.Plant != nil {
			p := def.Plant
			pollution := d.Pollution_POLLUTION_ANY
			if p.RequiresPollution {
				pollution = d.Pollution_POLLUTION_POLLUTED_ONLY
			} else if p.RequiresClean {
				pollution = d.Pollution_POLLUTION_CLEAN_ONLY
			}
			sowTags := p.SowTags
			if len(sowTags) == 0 {
				sowTags = []string{"Ground"}
			}
			t.Plant = &d.PlantProperties{GrowDays: p.GrowDays, FertilityMin: p.FertilityMin, FertilitySensitivity: p.FertilitySensitivity, GrowMinGlow: p.GrowMinGlow, HarvestWork: p.HarvestWork, SowTags: sowTags, HarvestYield: 1, HarvestedThingDef: def.Name + "_Product", Pollution: pollution}
			product := def.Name + "_Product"
			preferability := d.FoodPreferability_FOOD_PREFERABILITY_RAW_BAD
			if p.RawPreferred {
				preferability = d.FoodPreferability_FOOD_PREFERABILITY_RAW_TASTY
			}
			wire.ThingDefs = append(wire.ThingDefs, &d.ThingDef{DefName: product, ThingClass: fixtureThingClass, Ingestible: &d.IngestibleProperties{Preferability: preferability}})
			productFacts := &o.ThingDefFacts{DefName: product}
			if p.Edible {
				productFacts.FoodKind = o.FoodKind_FOOD_KIND_VEGETABLE.Enum()
			}
			wire.ThingFacts = append(wire.ThingFacts, productFacts)
			row(product, "", map[int32]float32{index(StatNutrition): p.HarvestNutrition, index(StatMarketValue): 1}, nil)
		}
		if len(def.Stuffs) > 0 || def.Stuffed {
			t.StuffCategories = []string{"Fixture"}
		}
		wire.ThingDefs = append(wire.ThingDefs, t)
		wire.ThingFacts = append(wire.ThingFacts, facts)
		if len(t.StuffCategories) == 0 {
			values := maps.Clone(apparelStats)
			if def.WorkToBuild != nil {
				values[index(StatWorkToBuild)] = *def.WorkToBuild
			}
			row(def.Name, "", values, def.Costs)
			continue
		}
		for _, stuff := range def.Stuffs {
			values := maps.Clone(apparelStats)
			for stat, value := range stuff.Stats {
				if slices.Contains(stats, stat) {
					values[index(stat)] = value
				}
			}
			row(def.Name, stuff.Stuff, values, stuff.Costs)
			items[stuff.Stuff] = true
		}
	}
	// Every cost and stuff the fixture names is an item def with a market value
	// of 1 unless the fixture defines it.
	for _, item := range slices.Sorted(maps.Keys(items)) {
		if named[item] || hasDef(wire.ThingDefs, item) {
			continue
		}
		wire.ThingDefs = append(wire.ThingDefs, &d.ThingDef{DefName: item, ThingClass: fixtureThingClass})
		wire.ThingFacts = append(wire.ThingFacts, &o.ThingDefFacts{DefName: item})
		row(item, "", map[int32]float32{index(StatMarketValue): 1}, nil)
	}
	wire.ThingDefs = append(wire.ThingDefs, &d.ThingDef{DefName: "Anchor", ThingClass: fixtureThingClass})
	wire.ThingFacts = append(wire.ThingFacts, &o.ThingDefFacts{DefName: "Anchor"})
	row("Anchor", "", nil, nil)
	for class, bases := range chains {
		wire.ClassChains = append(wire.ClassChains, &o.ClassChain{Name: class, Bases: bases})
	}
	slices.SortFunc(wire.ClassChains, func(a, b *o.ClassChain) int { return cmp.Compare(a.GetName(), b.GetName()) })
	return wire
}

// FixtureRoomStats are the Impressiveness room stat's stages as Core's
// RoomStats.xml states them, for a test catalog's def sets.
func FixtureRoomStats() []*d.RoomStatDef {
	stage := func(min float32, label string) *d.Opt_RoomStatScoreStage {
		return &d.Opt_RoomStatScoreStage{Value: &d.RoomStatScoreStage{MinScore: min, Label: label}}
	}
	return []*d.RoomStatDef{{DefName: RoomStatImpressiveness, ScoreStages: []*d.Opt_RoomStatScoreStage{
		stage(0, "awful"), stage(20, "dull"), stage(30, "mediocre"), stage(40, "decent"), stage(50, "slightly impressive"), stage(65, "somewhat impressive")}}}
}

func fixtureWork(v *float32) float32 {
	if v == nil {
		return 0
	}
	return *v
}

func hasDef(rows []*d.ThingDef, name string) bool {
	return slices.ContainsFunc(rows, func(r *d.ThingDef) bool { return r.GetDefName() == name })
}
