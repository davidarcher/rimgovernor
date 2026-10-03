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
	// Sittable makes the def a building a pawn sits on, with Comfort as its
	// Comfort stat (every stuff of the def shares it); EatSurface gives it an
	// eating surface.
	Sittable   bool
	Comfort    float32
	EatSurface bool
	// Weapon makes the def a weapon with the verb, projectile and tools it
	// states (#1723).
	Weapon *FixtureWeapon
	// Race makes the def a pawn race with the RaceProperties it states and
	// the game-computed race facts (#1722).
	Race *FixtureRace
}

// FixtureRace is a fixture race: its RaceProperties and the facts the game's
// race code computes.
type FixtureRace struct {
	Props *d.RaceProperties
	Facts *o.RaceFacts
}

// FixtureWeapon is a fixture weapon: a ranged one names its verb class and
// projectile, a melee one the capacities of its tools (one tool each).
type FixtureWeapon struct {
	// VerbClass is the ranged verb's class ("Verse.Verb_Shoot"); empty makes
	// the weapon a melee one.
	VerbClass string
	// Range and ForcedMissRadius are the verb's; ExplosionRadius, DamageDef
	// and Incendiary the projectile's (named Name+"_Projectile").
	Range, ForcedMissRadius, ExplosionRadius float32
	DamageDef                                string
	Incendiary                               bool
	// Capacities is the capacity of each of a melee weapon's tools.
	Capacities []string
}

// CoreWeaponFixtures are the Core weapons the planning tests name, stated the
// way the game's XML does (#1723).
func CoreWeaponFixtures() []FixtureDef {
	shoot, oneUse, thrown := "Verse.Verb_Shoot", "RimWorld.Verb_ShootOneUse", "Verse.Verb_LaunchProjectile"
	return []FixtureDef{
		{Name: "Weapon_GrenadeFrag", Weapon: &FixtureWeapon{VerbClass: thrown, Range: 12.9, ForcedMissRadius: 1.9, ExplosionRadius: 1.9, DamageDef: "Bomb"}},
		{Name: "Weapon_GrenadeMolotov", Weapon: &FixtureWeapon{VerbClass: thrown, Range: 12.9, ForcedMissRadius: 1.9, ExplosionRadius: 1.1, DamageDef: "Flame", Incendiary: true}},
		{Name: "Weapon_GrenadeEMP", Weapon: &FixtureWeapon{VerbClass: thrown, Range: 12.9, ForcedMissRadius: 1.9, ExplosionRadius: 3.5, DamageDef: "EMP"}},
		{Name: "Gun_EmpLauncher", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 23.9, ForcedMissRadius: 1.9, ExplosionRadius: 1.1, DamageDef: "EMP"}},
		{Name: "Gun_IncendiaryLauncher", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 23.9, ForcedMissRadius: 1.9, ExplosionRadius: 1.1, DamageDef: "Flame", Incendiary: true}},
		{Name: "Gun_SmokeLauncher", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 23.9, ForcedMissRadius: 1.9, ExplosionRadius: 2.4, DamageDef: "Smoke"}},
		{Name: "Gun_TripleRocket", Weapon: &FixtureWeapon{VerbClass: oneUse, Range: 35.9, ForcedMissRadius: 2.9, ExplosionRadius: 3.9, DamageDef: "Bomb"}},
		{Name: "Gun_DoomsdayRocket", Weapon: &FixtureWeapon{VerbClass: oneUse, Range: 35.9, ForcedMissRadius: 1.9, ExplosionRadius: 7.8, DamageDef: "Bomb"}},
		{Name: "Gun_AssaultRifle", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 30.9, DamageDef: "Bullet"}},
		{Name: "Gun_Minigun", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 30.9, DamageDef: "Bullet"}},
		{Name: "Gun_PumpShotgun", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 18.9, DamageDef: "Bullet"}},
		{Name: "Gun_SniperRifle", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 44.9, DamageDef: "Bullet"}},
		{Name: "Bow_Short", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 22.9, DamageDef: "Arrow"}},
		{Name: "MeleeWeapon_Club", Weapon: &FixtureWeapon{Capacities: []string{"Poke", "Blunt"}}},
		{Name: "MeleeWeapon_Mace", Weapon: &FixtureWeapon{Capacities: []string{"Poke", "Blunt"}}},
		{Name: "MeleeWeapon_Warhammer", Weapon: &FixtureWeapon{Capacities: []string{"Poke", "Blunt"}}},
		{Name: "MeleeWeapon_Knife", Weapon: &FixtureWeapon{Capacities: []string{"Cut", "Stab"}}},
		{Name: "MeleeWeapon_Spear", Weapon: &FixtureWeapon{Capacities: []string{"Poke", "Stab"}}},
		{Name: "MeleeWeapon_LongSword", Weapon: &FixtureWeapon{Capacities: []string{"Poke", "Cut", "Stab"}}},
	}
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
	stats := []string{StatMaxHitPoints, StatFlammability, StatBedRestEffectiveness, StatWorkToBuild, StatMarketValue, StatNutrition, StatCleanliness, StatBeauty, "ArmorRating_Sharp", "ArmorRating_Blunt", "Insulation_Cold", "Insulation_Heat", StatComfort}
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
	stuffNames := map[string]bool{}
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
				// The pin's own row: a thrower stands five cells out.
				t.Building.WatchBuildingStandDistanceRange = &d.IntRange{Min: 5, Max: 5}
			}
			chains[class] = []string{"RimWorld.JoyGiver"}
			chains["RimWorld.JoyGiver"] = nil
			job := "Play_" + def.Name
			wire.Defs.JobDefs = append(wire.Defs.JobDefs, &d.JobDef{DefName: job, JoyGainRate: 1, JoyDuration: 1000})
			wire.Defs.JoyGiverDefs = append(wire.Defs.JoyGiverDefs, &d.JoyGiverDef{DefName: job, GiverClass: class, ThingDefs: []string{def.Name}, JobDef: job})
		}
		if def.Sittable {
			if t.Building == nil {
				t.Building = &d.BuildingProperties{}
			}
			t.Building.IsSittable = true
			apparelStats[index(StatComfort)] = def.Comfort
		}
		if def.EatSurface {
			t.SurfaceType = d.SurfaceType_SURFACE_TYPE_EAT
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
		if w := def.Weapon; w != nil {
			if w.VerbClass != "" {
				projectile := def.Name + "_Projectile"
				t.Verbs = append(t.Verbs, &d.Opt_VerbProperties{Value: &d.VerbProperties{VerbClass: w.VerbClass, Range: w.Range, ForcedMissRadius: w.ForcedMissRadius, DefaultProjectile: projectile}})
				wire.ThingDefs = append(wire.ThingDefs, &d.ThingDef{DefName: projectile, ThingClass: fixtureThingClass, Projectile: &d.ProjectileProperties{DamageDef: w.DamageDef, ExplosionRadius: w.ExplosionRadius, Ai_IsIncendiary: w.Incendiary}})
				wire.ThingFacts = append(wire.ThingFacts, &o.ThingDefFacts{DefName: projectile})
				row(projectile, "", nil, nil)
				chains["Verse.Verb"] = nil
				chains["Verse.Verb_LaunchProjectile"] = []string{"Verse.Verb"}
				chains["Verse.Verb_Shoot"] = []string{"Verse.Verb_LaunchProjectile", "Verse.Verb"}
				chains["RimWorld.Verb_ShootOneUse"] = []string{"Verse.Verb_Shoot", "Verse.Verb_LaunchProjectile", "Verse.Verb"}
				chains["RimWorld.Verb_MeleeAttack"] = []string{"Verse.Verb"}
			}
			for _, capacity := range w.Capacities {
				t.Tools = append(t.Tools, &d.Opt_Tool{Value: &d.Tool{Capacities: []string{capacity}}})
			}
		}
		if r := def.Race; r != nil {
			t.Race, facts.Race = r.Props, r.Facts
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
			stuffNames[stuff.Stuff] = true
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
	// A stuff names the category the fixture's stuffed defs accept, so its rows
	// are allowed stuffs (StuffProperties.CanMake).
	for _, t := range wire.ThingDefs {
		if stuffNames[t.DefName] && t.StuffProps == nil {
			t.StuffProps = &d.StuffProperties{Categories: []string{"Fixture"}}
		}
	}
	wire.ThingDefs = append(wire.ThingDefs, &d.ThingDef{DefName: "Anchor", ThingClass: fixtureThingClass})
	wire.ThingFacts = append(wire.ThingFacts, &o.ThingDefFacts{DefName: "Anchor"})
	row("Anchor", "", nil, nil)
	FixtureEnvironmentDefs(wire)
	fixtureDamageDefs(wire)
	for class, bases := range chains {
		wire.ClassChains = append(wire.ClassChains, &o.ClassChain{Name: class, Bases: bases})
	}
	slices.SortFunc(wire.ClassChains, func(a, b *o.ClassChain) int { return cmp.Compare(a.GetName(), b.GetName()) })
	return wire
}

// FixtureEnvironmentDefs adds to a test catalog's wire form the weather and
// game condition rows the game's Core defs carry (accuracy multipliers, the
// electricity-disabling flare class), with the class chains they name.
func FixtureEnvironmentDefs(v *o.DefinitionCatalog) {
	if v.Defs == nil {
		v.Defs = &d.DefSets{}
	}
	for name, accuracy := range map[string]float32{"Clear": 1, "Rain": 0.8, "RainyThunderstorm": 0.8, "SnowGentle": 0.8, "SnowHard": 0.8, "Fog": 0.5, "FoggyRain": 0.5} {
		v.Defs.WeatherDefs = append(v.Defs.WeatherDefs, &d.WeatherDef{DefName: name, AccuracyMultiplier: accuracy})
	}
	const base = "RimWorld.GameCondition"
	classes := map[string]string{"SolarFlare": electricityDisabledClass, "ColdSnap": "RimWorld.GameCondition_TemperatureOffset", "HeatWave": "RimWorld.GameCondition_TemperatureOffset",
		"Eclipse": "RimWorld.GameCondition_Eclipse", "ToxicFallout": "RimWorld.GameCondition_ToxicFallout", "VolcanicWinter": "RimWorld.GameCondition_VolcanicWinter", "PsychicDrone": "RimWorld.GameCondition_PsychicEmanator"}
	seen := map[string]bool{base: true}
	v.ClassChains = append(v.ClassChains, &o.ClassChain{Name: base})
	for name, class := range classes {
		v.Defs.GameConditionDefs = append(v.Defs.GameConditionDefs, &d.GameConditionDef{DefName: name, ConditionClass: class})
		if !seen[class] {
			seen[class] = true
			v.ClassChains = append(v.ClassChains, &o.ClassChain{Name: class, Bases: []string{base}})
		}
	}
}

// fixtureDamageDefs adds the Core damage defs a weapon's projectile or melee
// maneuver names, and a maneuver for each melee tool capacity (#1723).
func fixtureDamageDefs(v *o.DefinitionCatalog) {
	for _, def := range []*d.DamageDef{
		{DefName: "Bomb", HarmsHealth: true}, {DefName: "Flame", HarmsHealth: true}, {DefName: "Bullet", HarmsHealth: true}, {DefName: "Arrow", HarmsHealth: true},
		{DefName: "Smoke"}, {DefName: "EMP", CauseStun: true, ExternalViolenceForMechanoids: true},
		{DefName: "Cut", HarmsHealth: true, ArmorCategory: "Sharp"}, {DefName: "Stab", HarmsHealth: true, ArmorCategory: "Sharp"},
		{DefName: "Blunt", HarmsHealth: true, ArmorCategory: "Blunt"}, {DefName: "Poke", HarmsHealth: true, ArmorCategory: "Blunt"},
	} {
		v.Defs.DamageDefs = append(v.Defs.DamageDefs, def)
	}
	for _, capacity := range []string{"Cut", "Stab", "Blunt", "Poke"} {
		v.Defs.ManeuverDefs = append(v.Defs.ManeuverDefs, &d.ManeuverDef{DefName: capacity + "Maneuver", RequiredCapacity: capacity, Verb: &d.VerbProperties{MeleeDamageDef: capacity}})
	}
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
