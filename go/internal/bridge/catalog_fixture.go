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

// FixtureDef states one buildable or sowable def of a test catalog:
// the facts a planner's test cares about, written the way the game's rows
// carry them, so that FixtureCatalog can lay them out as the def rows, stat
// table and thing facts the planning views read.
type FixtureDef struct {
	Name string
	// Shell makes the def a mortar shell (projectileWhenLoaded).
	Shell *FixtureShell
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
	// TempControlW adds a CompProperties_TempControl with that energyPerSecond
	// (positive heats, negative cools).
	TempControlW *float32
	// MechCharger gives the def a thing class derived from Building_MechCharger.
	MechCharger bool
	// GeneBank gives the def the genepack container comp.
	GeneBank bool
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
	// WorkTableRole makes the def a work table scored into that room role
	// (workTableRoomRole), worked from the cell in front of it; Bench gives
	// a def that states no role the same interaction cell; Bed makes it a
	// humanlike bed counted for bedrooms and barracks, Medical one that is
	// medical by default.
	WorkTableRole string
	Bench         bool
	Bed, Medical  bool
	// Animal makes the def a bed for animals (a Building_Bed that is not
	// humanlike); both kinds of bed take any body size and state Comfort as
	// their Comfort stat. Sarcophagus makes it a Building_Sarcophagus.
	Animal, Sarcophagus bool
	// Door makes the def a Building_Door; AnimalFlap one roaming animals can
	// open (BuildingProperties.roamerCanOpen), the animal flap.
	Door, AnimalFlap bool
	// Facility makes the def a facility (CompProperties_Facility); Links lists
	// the facilities its CompProperties_AffectedByFacilities may link.
	Facility *FixtureFacility
	Links    []string
	// Weapon makes the def a weapon with the verb, projectile and tools it
	// states.
	Weapon *FixtureWeapon
	// Race makes the def a pawn race with the RaceProperties it states and
	// the game-computed race facts.
	Race *FixtureRace
	// BillWork makes the def a player-buildable bill giver (a Building_WorkTable
	// building) that a DoBill work giver of this work type serves.
	BillWork string
}

// FixtureFacility is a fixture def's CompProperties_Facility: the stat it
// offsets on what it links, and the link rules.
type FixtureFacility struct {
	Offsets         map[string]float32
	MaxDistance     float32
	MaxSimultaneous int32
	// Adjacent is mustBePlacedAdjacent, CardinalToHead the bed-head variant.
	Adjacent, CardinalToHead bool
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
	// Makeshift keeps the weapon out of the Weapons thing category: a def that
	// is a club by its tools alone (WoodLog).
	Makeshift bool
	// Market is the weapon's MarketValue at Normal quality; zero reads as 1.
	Market float32
	// Range and ForcedMissRadius are the verb's; ExplosionRadius, DamageDef
	// and Incendiary the projectile's (named Name+"_Projectile").
	Range, ForcedMissRadius, ExplosionRadius float32
	DamageDef                                string
	Incendiary                               bool
	// Capacities is the capacity of each of a melee weapon's tools: one
	// stateless tool each (no power).
	Capacities []string
	// Tools are a melee weapon's tools as the game's XML states them.
	Tools []FixtureTool
	// Warmup is the verb's warmupTime; Cooldown the RangedWeapon_Cooldown stat;
	// Burst the burstShotCount (0 is 1) and BurstGap the ticksBetweenBurstShots.
	Warmup, Cooldown float32
	Burst, BurstGap  int32
	// Damage is the projectile's damageAmountBase (0 leaves it to the damage
	// def, as the game's -1 does); AP its armorPenetrationBase (nil is the
	// game's unstated -1).
	Damage float32
	AP     *float32
	// Accuracy are the AccuracyTouch, Short, Medium and Long stats; all zero
	// states none.
	Accuracy [4]float32
}

// FixtureShell is a mortar shell's projectile (named Name+"_Projectile"):
// the damage def it explodes with, its explosionRadius and whether it is
// incendiary.
type FixtureShell struct {
	DamageDef       string
	ExplosionRadius float32
	Incendiary      bool
}

// FixtureTool is a melee tool: its capacities, power, cooldownTime,
// armorPenetration (nil is the game's unstated -1) and chanceFactor (0 is 1).
type FixtureTool struct {
	Capacities   []string
	Power        float32
	Cooldown     float32
	AP           *float32
	ChanceFactor float32
}

// CoreWeaponFixtures are the Core weapons the planning tests name, stated the
// way the game's XML does.
func CoreWeaponFixtures() []FixtureDef {
	shoot, oneUse, thrown := "Verse.Verb_Shoot", "RimWorld.Verb_ShootOneUse", "Verse.Verb_LaunchProjectile"
	return []FixtureDef{
		{Name: "Weapon_GrenadeFrag", Weapon: &FixtureWeapon{VerbClass: thrown, Range: 12.9, ForcedMissRadius: 1.9, ExplosionRadius: 1.9, DamageDef: "Bomb", Warmup: 1.5, Cooldown: 2.66}},
		{Name: "Weapon_GrenadeMolotov", Weapon: &FixtureWeapon{VerbClass: thrown, Range: 12.9, ForcedMissRadius: 1.9, ExplosionRadius: 1.1, DamageDef: "Flame", Incendiary: true, Warmup: 1.5, Cooldown: 2.66}},
		{Name: "Weapon_GrenadeEMP", Weapon: &FixtureWeapon{VerbClass: thrown, Range: 12.9, ForcedMissRadius: 1.9, ExplosionRadius: 3.5, DamageDef: "EMP", Warmup: 1.5, Cooldown: 2.66}},
		{Name: "Gun_EmpLauncher", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 23.9, ForcedMissRadius: 1.9, ExplosionRadius: 1.1, DamageDef: "EMP", Warmup: 3.5, Cooldown: 3.5}},
		{Name: "Gun_IncendiaryLauncher", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 23.9, ForcedMissRadius: 1.9, ExplosionRadius: 1.1, DamageDef: "Flame", Incendiary: true, Warmup: 3.5, Cooldown: 3.5}},
		{Name: "Gun_SmokeLauncher", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 23.9, ForcedMissRadius: 1.9, ExplosionRadius: 2.4, DamageDef: "Smoke", Warmup: 3.5, Cooldown: 4.5}},
		{Name: "Gun_TripleRocket", Weapon: &FixtureWeapon{VerbClass: oneUse, Range: 35.9, ForcedMissRadius: 2.9, ExplosionRadius: 3.9, DamageDef: "Bomb", Warmup: 4.5, Cooldown: 4.5, Burst: 3, BurstGap: 20}},
		{Name: "Gun_DoomsdayRocket", Weapon: &FixtureWeapon{VerbClass: oneUse, Range: 35.9, ForcedMissRadius: 1.9, ExplosionRadius: 7.8, DamageDef: "Bomb", Warmup: 4.5, Cooldown: 4.5}},
		{Name: "Gun_AssaultRifle", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 30.9, DamageDef: "Bullet", Warmup: 1, Cooldown: 1.7, Burst: 3, BurstGap: 10, Damage: 11, Accuracy: [4]float32{.6, .7, .65, .55}}},
		{Name: "Gun_Minigun", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 30.9, DamageDef: "Bullet", Warmup: 2.5, Cooldown: 1.5, Burst: 25, BurstGap: 5, Damage: 10, Accuracy: [4]float32{.2, .25, .25, .18}}},
		{Name: "Gun_LMG", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 25.9, DamageDef: "Bullet", Warmup: 1.8, Cooldown: 1.6, Burst: 6, BurstGap: 7, Damage: 12, Accuracy: [4]float32{.4, .48, .35, .26}}},
		{Name: "Gun_PumpShotgun", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 15.9, DamageDef: "Bullet", Warmup: .9, Cooldown: 1.25, Damage: 18, AP: ptr(float32(.14)), Accuracy: [4]float32{.8, .87, .77, .64}}},
		{Name: "Gun_SniperRifle", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 44.9, DamageDef: "Bullet", Warmup: 3.5, Cooldown: 1.5, Damage: 25, Accuracy: [4]float32{.5, .7, .88, .9}}},
		{Name: "Gun_BoltActionRifle", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 36.9, DamageDef: "Bullet", Warmup: 1.7, Cooldown: 1.5, Damage: 18, Accuracy: [4]float32{.65, .8, .9, .8}}},
		{Name: "Gun_Revolver", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 25.9, DamageDef: "Bullet", Warmup: .3, Cooldown: 1.6, Damage: 12, Accuracy: [4]float32{.8, .75, .55, .4}}},
		{Name: "Gun_ChargeRifle", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 27.9, DamageDef: "Bullet", Warmup: 1, Cooldown: 2, Burst: 3, BurstGap: 12, Damage: 16, AP: ptr(float32(.35)), Accuracy: [4]float32{.55, .64, .55, .45}}},
		{Name: "Bow_Short", Weapon: &FixtureWeapon{VerbClass: shoot, Range: 22.9, DamageDef: "Arrow", Warmup: 1.35, Cooldown: 1.65, Damage: 11, Accuracy: [4]float32{.75, .65, .45, .25}}},
		{Name: "MeleeWeapon_Club", Weapon: &FixtureWeapon{Tools: []FixtureTool{{Capacities: []string{"Poke"}, Power: 9, Cooldown: 2}, {Capacities: []string{"Blunt"}, Power: 14, Cooldown: 2}}}},
		{Name: "MeleeWeapon_Mace", Weapon: &FixtureWeapon{Tools: []FixtureTool{{Capacities: []string{"Poke"}, Power: 9, Cooldown: 2}, {Capacities: []string{"Blunt"}, Power: 15.7, Cooldown: 2}}}},
		{Name: "MeleeWeapon_Warhammer", Weapon: &FixtureWeapon{Capacities: []string{"Poke", "Blunt"}}},
		{Name: "MeleeWeapon_Knife", Weapon: &FixtureWeapon{Tools: []FixtureTool{{Capacities: []string{"Blunt"}, Power: 9, Cooldown: 2}, {Capacities: []string{"Cut"}, Power: 12, Cooldown: 1.5}, {Capacities: []string{"Stab"}, Power: 13, Cooldown: 2}}}},
		{Name: "MeleeWeapon_Spear", Weapon: &FixtureWeapon{Tools: []FixtureTool{{Capacities: []string{"Blunt", "Poke"}, Power: 13, Cooldown: 2.6}, {Capacities: []string{"Stab"}, Power: 23, Cooldown: 2.6, AP: ptr(float32(.5))}}}},
		{Name: "MeleeWeapon_LongSword", Weapon: &FixtureWeapon{Tools: []FixtureTool{{Capacities: []string{"Blunt"}, Power: 9, Cooldown: 2}, {Capacities: []string{"Stab"}, Power: 23, Cooldown: 2.6}, {Capacities: []string{"Cut"}, Power: 23, Cooldown: 2.6}}}},
		{Name: "WoodLog", Weapon: &FixtureWeapon{Makeshift: true, Tools: []FixtureTool{{Capacities: []string{"Blunt"}, Power: 10, Cooldown: 2}}}},
		// The mortar shells of Core, Biotech and Anomaly (every def with a
		// projectileWhenLoaded), by their projectile's damage def and radius.
		{Name: "Shell_HighExplosive", Shell: &FixtureShell{DamageDef: "Bomb", ExplosionRadius: 2.9}},
		{Name: "Shell_Incendiary", Shell: &FixtureShell{DamageDef: "Flame", ExplosionRadius: 2.9, Incendiary: true}},
		{Name: "Shell_EMP", Shell: &FixtureShell{DamageDef: "EMP", ExplosionRadius: 8.9}},
		{Name: "Shell_Smoke", Shell: &FixtureShell{DamageDef: "Smoke", ExplosionRadius: 7.2}},
		{Name: "Shell_Firefoam", Shell: &FixtureShell{DamageDef: "Extinguish", ExplosionRadius: 5}},
		{Name: "Shell_AntigrainWarhead", Shell: &FixtureShell{DamageDef: "BombSuper", ExplosionRadius: 14.9}},
		{Name: "Shell_Toxic", Shell: &FixtureShell{DamageDef: "ToxGas", ExplosionRadius: 4}},
		{Name: "Shell_Deadlife", Shell: &FixtureShell{DamageDef: "DeadlifeDust", ExplosionRadius: 0.1}},
	}
}

// WithCoreFurniture is defs plus the Core furniture rows (CoreFurnitureFixtures)
// a fixture catalog needs for PieceShapes: a def the caller already states
// keeps its own costs and research and takes the core row's size, role, bed
// flags, comfort and facility links; a caller's def that states no cost takes
// the core row's too (a def with none is free, which ranks a bed last).
func WithCoreFurniture(defs []FixtureDef) []FixtureDef {
	out := slices.Clone(defs)
	at := map[string]int{}
	for i, def := range out {
		at[def.Name] = i
	}
	for _, core := range CoreFurnitureFixtures() {
		i, ok := at[core.Name]
		if !ok {
			out = append(out, core)
			continue
		}
		out[i].Width, out[i].Height = core.Width, core.Height
		out[i].WorkTableRole, out[i].Bench, out[i].Bed, out[i].Medical = core.WorkTableRole, core.Bench, core.Bed, core.Medical
		out[i].Door, out[i].AnimalFlap = core.Door, core.AnimalFlap
		out[i].Animal, out[i].Sarcophagus, out[i].Comfort, out[i].Facility, out[i].Links = core.Animal, core.Sarcophagus, core.Comfort, core.Facility, core.Links
		if len(out[i].Costs) == 0 && len(out[i].Stuffs) == 0 && !out[i].Stuffed {
			out[i].Costs = core.Costs
		}
		out[i].Stuffs = slices.Clone(out[i].Stuffs)
		for j := range out[i].Stuffs {
			if len(out[i].Stuffs[j].Costs) == 0 {
				out[i].Stuffs[j].Costs = core.Costs
			}
		}
	}
	return out
}

// CoreFurnitureFixtures are the Core furniture the interior templates plan,
// stated the way the game's rows carry it (size, work table room role, bed
// flags and comfort, cost, facility links and the interaction cell in front
// of a bench): the rows a fixture catalog needs for PieceShapes to choose the
// room furniture (DefinitionCatalog.RoomFurniture) and validate. The costs
// and comforts are round numbers that rank the beds and benches as Core's do.
func CoreFurnitureFixtures() []FixtureDef {
	wood := func(n int64) []policy.Amount { return []policy.Amount{{Resource: "WoodLog", Count: n}} }
	links := []string{"EndTable", "Dresser"}
	benchLinks := []string{"ToolCabinet"}
	return []FixtureDef{
		{Name: "Bed", Width: 1, Height: 2, Bed: true, Comfort: .75, Costs: wood(40), Links: links},
		{Name: "Bedroll", Width: 1, Height: 2, Bed: true, Comfort: .5, Costs: wood(40)},
		{Name: "SleepingSpot", Width: 1, Height: 2, Bed: true, Comfort: .3},
		{Name: "DoubleBed", Width: 2, Height: 2, Bed: true, Comfort: .75, Costs: wood(80), Links: links},
		{Name: "BedrollDouble", Width: 2, Height: 2, Bed: true, Comfort: .5, Costs: wood(80)},
		{Name: "RoyalBed", Width: 2, Height: 2, Bed: true, Comfort: .9, Costs: wood(150), Links: links},
		{Name: "DoubleSleepingSpot", Width: 2, Height: 2, Bed: true, Comfort: .3},
		{Name: "HospitalBed", Width: 1, Height: 2, Bed: true, Medical: true, Comfort: .8, Costs: wood(60), Links: []string{"VitalsMonitor", "EndTable", "Dresser"}},
		{Name: "Door", Door: true, Costs: wood(25)},
		{Name: "AnimalFlap", Door: true, AnimalFlap: true, Costs: wood(25)},
		{Name: "AnimalSleepingSpot", Animal: true, Comfort: .3},
		{Name: "AnimalBed", Animal: true, Comfort: .7, Costs: wood(30)},
		{Name: "EndTable", Width: 1, Height: 1, Costs: wood(30), Facility: &FixtureFacility{Offsets: map[string]float32{StatComfort: .03}, MaxDistance: 8, MaxSimultaneous: 1, Adjacent: true, CardinalToHead: true}},
		{Name: "Dresser", Width: 2, Height: 1, Costs: wood(50), Facility: &FixtureFacility{Offsets: map[string]float32{StatComfort: .02}, MaxDistance: 6, MaxSimultaneous: 1}},
		{Name: "StandingLamp", Width: 1, Height: 1, Costs: wood(20)},
		{Name: "Heater", Width: 1, Height: 1, Costs: wood(30), PowerW: ptr(175.0), TempControlW: ptr(float32(21))},
		{Name: "Cooler", Width: 2, Height: 1, Costs: wood(40), PowerW: ptr(200.0), TempControlW: ptr(float32(-12))},
		{Name: "ToolCabinet", Width: 2, Height: 1, Costs: wood(40), Facility: &FixtureFacility{Offsets: map[string]float32{StatWorkTableWorkSpeedFactor: .06}, MaxDistance: 8, MaxSimultaneous: 2}},
		{Name: "ShelfSmall", Width: 1, Height: 1, Costs: wood(20)},
		{Name: "Campfire", Width: 1, Height: 1, Costs: wood(300)},
		{Name: "CraftingSpot", Width: 1, Height: 1},
		{Name: "PartySpot", Width: 1, Height: 1},
		{Name: "PassiveCooler", Width: 1, Height: 1, Costs: wood(50)},
		{Name: "VitalsMonitor", Width: 1, Height: 1, Costs: wood(100), Facility: &FixtureFacility{Offsets: map[string]float32{StatMedicalTendQualityOffset: .06}, MaxDistance: 8, MaxSimultaneous: 1, Adjacent: true}},
		{Name: "Sarcophagus", Width: 1, Height: 2, Sarcophagus: true, Costs: wood(100)},
		{Name: "FueledStove", Width: 3, Height: 1, WorkTableRole: "Kitchen", Costs: wood(80)},
		{Name: "ElectricStove", Width: 3, Height: 1, WorkTableRole: "Kitchen", Costs: wood(100)},
		{Name: "TableStonecutter", Width: 3, Height: 1, WorkTableRole: "Workshop", Costs: wood(40), Links: benchLinks},
		{Name: "ElectricSmithy", Width: 3, Height: 1, WorkTableRole: "Workshop", Costs: wood(200), Links: benchLinks},
		{Name: "HandTailoringBench", Width: 3, Height: 1, WorkTableRole: "Workshop", Costs: wood(50), Links: benchLinks},
		{Name: "FabricationBench", Width: 5, Height: 2, WorkTableRole: "Workshop", Costs: wood(400), Links: benchLinks},
		{Name: "TableButcher", Width: 3, Height: 1, Bench: true, Costs: wood(50)},
		{Name: "SimpleResearchBench", Width: 3, Height: 2, WorkTableRole: "Laboratory", Costs: wood(50)},
		{Name: "HiTechResearchBench", Width: 5, Height: 2, WorkTableRole: "Laboratory", Costs: wood(300)},
	}
}

// ptr is a pointer to v, for the optional fixture fields.
func ptr[T any](v T) *T { return &v }

// FixtureApparel is a garment's layers, covered body part groups, outfit tags
// and armor ratings (every stuff of the def shares them).
type FixtureApparel struct {
	Layers, Groups, Tags []string
	Sharp, Blunt         float32
	// Market is the garment's MarketValue at Normal quality; zero reads as
	// 100, which hides price differences, so a test that prices gear states it.
	Market float32
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
	// SowMinSkill is the sowing skill floor; Yield the units per harvest (0
	// means 1); Persists keeps the plant standing after a harvest.
	SowMinSkill int32
	Yield       float32
	Persists    bool
}

func (p *FixturePlant) yield() float32 {
	if p.Yield == 0 {
		return 1
	}
	return p.Yield
}

func (p *FixturePlant) persistAfter() float32 {
	if p.Persists {
		return 0.5
	}
	return 0
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
	// fixtureBedBodySize is the body size limit every fixture bed states,
	// the game's "any pawn" (9999 on Core).
	fixtureBedBodySize = 9999
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
	wire.Defs = &d.DefSets{StatDefs: []*d.StatDef{{DefName: StatMarketValue}, {DefName: statRangedCooldown, DefaultBaseValue: 1}, {DefName: statRangedDamageMult, DefaultBaseValue: 1}, {DefName: statRangedPenMult, DefaultBaseValue: 1}, {DefName: statMeleeCooldown, DefaultBaseValue: 1}, {DefName: statMeleeDamageMult, DefaultBaseValue: 1}}, RoomStatDefs: FixtureRoomStats(), ThingCategoryDefs: []*d.ThingCategoryDef{{DefName: categoryWeapons}}}
	wire.Constants = &o.CatalogConstants{TicksPerHour: 2500, TicksPerDay: 60000, DaysPerYear: 60, BillStackMax: 15, SkillMaxLevel: 20, LitGlowThreshold: 0.3, FullRotRateC: 10, RoofMaxSupportDistance: 6.9, CurrencyDef: "Silver", WortDef: "Wort"}
	wire.TerrainDefs = []*d.TerrainDef{{DefName: "AnchorTerrain"}}
	wire.StatValues.TerrainRows = []*o.DefStatRow{{DefName: "AnchorTerrain", Stat: []int32{index(StatCleanliness), index(StatBeauty), index(StatFlammability)}, Value: []float32{0, 0, 0}}}
	chains := map[string][]string{fixtureThingClass: nil, fixtureChargerClass: {"RimWorld.Building_MechCharger"}, "RimWorld.Building_MechCharger": nil, classVolumeGetter: nil, classNutritionGetter: nil, "RimWorld.Precept": nil}
	for _, message := range []proto.Message{&d.CompProperties_Power{}, &d.CompProperties_Glower{}, &d.CompProperties_Explosive{}, &d.CompProperties_Facility{}, &d.CompProperties_AffectedByFacilities{}, &d.CompProperties_TempControl{}, &d.CompProperties_GenepackContainer{}} {
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
		if def.BillWork != "" {
			t.Category, t.ThingClass = d.ThingCategory_THING_CATEGORY_BUILDING, classBillGiverBuilding
			chains[classBillGiverBuilding], chains[classDoBillGiver] = nil, nil
			wire.Defs.WorkGiverDefs = append(wire.Defs.WorkGiverDefs, &d.WorkGiverDef{DefName: "DoBills" + def.Name, GiverClass: classDoBillGiver, WorkType: def.BillWork, FixedBillGiverDefs: []string{def.Name}})
		}
		apparelStats := map[int32]float32{}
		if a := def.Apparel; a != nil {
			t.Apparel = &d.ApparelProperties{Layers: a.Layers, BodyPartGroups: a.Groups, DefaultOutfitTags: a.Tags, DevelopmentalStageFilter: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT}
			apparelStats = map[int32]float32{index("ArmorRating_Sharp"): a.Sharp, index("ArmorRating_Blunt"): a.Blunt, index("Insulation_Cold"): 0, index("Insulation_Heat"): 0, index(StatMarketValue): cmp.Or(a.Market, 100)}
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
		if def.WorkTableRole != "" || def.Bench {
			if t.Building == nil {
				t.Building = &d.BuildingProperties{}
			}
			t.Building.WorkTableRoomRole = def.WorkTableRole
			t.HasInteractionCell, t.InteractionCellOffset = true, &d.IntVec3{Z: -1}
		}
		if def.Bed || def.Animal {
			if t.Building == nil {
				t.Building = &d.BuildingProperties{}
			}
			t.ThingClass = ClassBed
			chains[ClassBed] = nil
			t.Building.BedMaxBodySize = fixtureBedBodySize
			if def.Bed {
				t.Building.BedHumanlike, t.Building.BedCountsForBedroomOrBarracks, t.Building.BedDefaultMedical = true, !def.Medical, def.Medical
			}
			if def.Comfort != 0 {
				apparelStats[index(StatComfort)] = def.Comfort
			}
		}
		if def.Sarcophagus {
			t.ThingClass = ClassSarcophagus
			chains[ClassSarcophagus] = nil
		}
		if def.Door || def.AnimalFlap {
			if t.Building == nil {
				t.Building = &d.BuildingProperties{}
			}
			t.ThingClass = ClassDoor
			chains[ClassDoor] = nil
			t.Building.RoamerCanOpen = def.AnimalFlap
		}
		if def.EatSurface {
			t.SurfaceType = d.SurfaceType_SURFACE_TYPE_EAT
		}
		comp := func(value *d.CompPropertiesAny) { t.Comps = append(t.Comps, &d.Opt_CompPropertiesAny{Value: value}) }
		if f := def.Facility; f != nil {
			facility := &d.CompProperties_Facility{MaxDistance: f.MaxDistance, MaxSimultaneous: f.MaxSimultaneous, MustBePlacedAdjacent: f.Adjacent, MustBePlacedAdjacentCardinalToBedHead: f.CardinalToHead}
			for _, stat := range slices.Sorted(maps.Keys(f.Offsets)) {
				facility.StatOffsets = append(facility.StatOffsets, &d.Opt_StatModifier{Value: &d.StatModifier{Stat: stat, Value: f.Offsets[stat]}})
			}
			comp(&d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Facility{CompProperties_Facility: facility}})
		}
		if len(def.Links) > 0 {
			comp(&d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_AffectedByFacilities{CompProperties_AffectedByFacilities: &d.CompProperties_AffectedByFacilities{LinkableFacilities: def.Links}}})
		}
		if def.PowerW != nil {
			comp(&d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Power{CompProperties_Power: &d.CompProperties_Power{BasePowerConsumption: float32(*def.PowerW)}}})
		}
		if def.GeneBank {
			comp(&d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_GenepackContainer{CompProperties_GenepackContainer: &d.CompProperties_GenepackContainer{MaxCapacity: 4}}})
		}
		if def.TempControlW != nil {
			comp(&d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_TempControl{CompProperties_TempControl: &d.CompProperties_TempControl{EnergyPerSecond: *def.TempControlW}}})
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
			t.Plant = &d.PlantProperties{GrowDays: p.GrowDays, FertilityMin: p.FertilityMin, FertilitySensitivity: p.FertilitySensitivity, GrowMinGlow: p.GrowMinGlow, HarvestWork: p.HarvestWork, SowTags: sowTags, HarvestYield: p.yield(), SowMinSkill: p.SowMinSkill, HarvestAfterGrowth: p.persistAfter(), HarvestedThingDef: def.Name + "_Product", Pollution: pollution}
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
		if s := def.Shell; s != nil {
			projectile := def.Name + "_Projectile"
			t.ProjectileWhenLoaded = projectile
			wire.ThingDefs = append(wire.ThingDefs, &d.ThingDef{DefName: projectile, ThingClass: fixtureThingClass, Projectile: &d.ProjectileProperties{DamageDef: s.DamageDef, DamageAmountBase: -1, ArmorPenetrationBase: -1, ExplosionRadius: s.ExplosionRadius, Ai_IsIncendiary: s.Incendiary}})
			wire.ThingFacts = append(wire.ThingFacts, &o.ThingDefFacts{DefName: projectile})
			row(projectile, "", nil, nil)
		}
		if w := def.Weapon; w != nil {
			if !w.Makeshift {
				t.ThingCategories = []string{categoryWeapons}
			}
			if w.VerbClass != "" {
				projectile := def.Name + "_Projectile"
				burst := w.Burst
				if burst == 0 {
					burst = 1
				}
				t.Verbs = append(t.Verbs, &d.Opt_VerbProperties{Value: &d.VerbProperties{VerbClass: w.VerbClass, Range: w.Range, ForcedMissRadius: w.ForcedMissRadius, DefaultProjectile: projectile, WarmupTime: w.Warmup, BurstShotCount: burst, TicksBetweenBurstShots: w.BurstGap}})
				stat := func(name string, value float32) {
					t.StatBases = append(t.StatBases, &d.Opt_StatModifier{Value: &d.StatModifier{Stat: name, Value: value}})
				}
				stat(statRangedCooldown, w.Cooldown)
				if w.Accuracy != [4]float32{} {
					stat("AccuracyTouch", w.Accuracy[0])
					stat(statAccuracyShort, w.Accuracy[1])
					stat(statAccuracyMedium, w.Accuracy[2])
					stat(statAccuracyLong, w.Accuracy[3])
				}
				damage, ap := float32(-1), float32(-1)
				if w.Damage != 0 {
					damage = w.Damage
				}
				if w.AP != nil {
					ap = *w.AP
				}
				wire.ThingDefs = append(wire.ThingDefs, &d.ThingDef{DefName: projectile, ThingClass: fixtureThingClass, Projectile: &d.ProjectileProperties{DamageDef: w.DamageDef, DamageAmountBase: int32(damage), ArmorPenetrationBase: ap, ExplosionRadius: w.ExplosionRadius, Ai_IsIncendiary: w.Incendiary}})
				wire.ThingFacts = append(wire.ThingFacts, &o.ThingDefFacts{DefName: projectile})
				row(projectile, "", nil, nil)
				chains["Verse.Verb"] = nil
				chains["Verse.Verb_LaunchProjectile"] = []string{"Verse.Verb"}
				chains["Verse.Verb_Shoot"] = []string{"Verse.Verb_LaunchProjectile", "Verse.Verb"}
				chains["RimWorld.Verb_ShootOneUse"] = []string{"Verse.Verb_Shoot", "Verse.Verb_LaunchProjectile", "Verse.Verb"}
				chains["RimWorld.Verb_MeleeAttack"] = []string{"Verse.Verb"}
			}
			for _, capacity := range w.Capacities {
				t.Tools = append(t.Tools, &d.Opt_Tool{Value: &d.Tool{Capacities: []string{capacity}, Power: 10, CooldownTime: 2, ArmorPenetration: -1, ChanceFactor: 1}})
			}
			for _, tool := range w.Tools {
				ap, chance := float32(-1), float32(1)
				if tool.AP != nil {
					ap = *tool.AP
				}
				if tool.ChanceFactor != 0 {
					chance = tool.ChanceFactor
				}
				t.Tools = append(t.Tools, &d.Opt_Tool{Value: &d.Tool{Capacities: tool.Capacities, Power: tool.Power, CooldownTime: tool.Cooldown, ArmorPenetration: ap, ChanceFactor: chance}})
			}
		}
		if r := def.Race; r != nil {
			t.Race, facts.Race = r.Props, r.Facts
		}
		wire.ThingDefs = append(wire.ThingDefs, t)
		wire.ThingFacts = append(wire.ThingFacts, facts)
		if len(t.StuffCategories) == 0 {
			values := maps.Clone(apparelStats)
			if _, priced := values[index(StatMarketValue)]; def.Weapon != nil && !priced {
				// A weapon is an item too (WoodLog is both a club and a stuff).
				values[index(StatMarketValue)] = cmp.Or(def.Weapon.Market, 1)
			}
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
	// Two biomes: FixtureDarkBiome carries a condition of a subclass of the
	// no-sunlight family (a mod's), FixtureLitBiome an unrelated one.
	classes["FixtureDarkness"] = "FixtureMod.GameCondition_PermanentDark"
	classes["FixtureBreeze"] = "RimWorld.GameCondition_TemperatureOffset"
	v.Defs.BiomeDefs = append(v.Defs.BiomeDefs, &d.BiomeDef{DefName: "FixtureDarkBiome", BiomeMapConditions: []string{"FixtureBreeze", "FixtureDarkness"}},
		&d.BiomeDef{DefName: "FixtureLitBiome", BiomeMapConditions: []string{"FixtureBreeze"}}, &d.BiomeDef{DefName: "FixtureBareBiome"})
	seen := map[string]bool{base: true}
	v.ClassChains = append(v.ClassChains, &o.ClassChain{Name: base}, &o.ClassChain{Name: noSunlightClass, Bases: []string{base}},
		&o.ClassChain{Name: "FixtureMod.GameCondition_PermanentDark", Bases: []string{noSunlightClass, base}})
	seen[noSunlightClass], seen["FixtureMod.GameCondition_PermanentDark"] = true, true
	for name, class := range classes {
		v.Defs.GameConditionDefs = append(v.Defs.GameConditionDefs, &d.GameConditionDef{DefName: name, ConditionClass: class})
		if !seen[class] {
			seen[class] = true
			v.ClassChains = append(v.ClassChains, &o.ClassChain{Name: class, Bases: []string{base}})
		}
	}
}

// fixtureDamageDefs adds the Core damage defs a weapon's projectile or melee
// maneuver names, and a maneuver for each melee tool capacity.
func fixtureDamageDefs(v *o.DefinitionCatalog) {
	v.Defs.DamageDefs = append(v.Defs.DamageDefs, []*d.DamageDef{
		{DefName: "Bomb", HarmsHealth: true, IsExplosive: true, DefaultDamage: 50, DefaultArmorPenetration: 0.1, ArmorCategory: "Sharp"},
		{DefName: "BombSuper", HarmsHealth: true, IsExplosive: true, DefaultDamage: 550, DefaultArmorPenetration: 0.1, ArmorCategory: "Sharp"},
		{DefName: "Extinguish"}, {DefName: "ToxGas"}, {DefName: "DeadlifeDust"},
		{DefName: "Flame", HarmsHealth: true, DefaultDamage: 10, ArmorCategory: "Heat"},
		{DefName: "Bullet", HarmsHealth: true, ArmorCategory: "Sharp"}, {DefName: "Arrow", HarmsHealth: true, ArmorCategory: "Sharp"},
		{DefName: "Smoke"}, {DefName: "EMP", CauseStun: true, ExternalViolenceForMechanoids: true, DefaultDamage: 50},
		{DefName: "Cut", HarmsHealth: true, ArmorCategory: "Sharp"}, {DefName: "Stab", HarmsHealth: true, ArmorCategory: "Sharp"},
		{DefName: "Blunt", HarmsHealth: true, ArmorCategory: "Blunt"}, {DefName: "Poke", HarmsHealth: true, ArmorCategory: "Blunt"},
	}...)
	for _, capacity := range []string{"Cut", "Stab", "Blunt", "Poke"} {
		v.Defs.ManeuverDefs = append(v.Defs.ManeuverDefs, &d.ManeuverDef{DefName: capacity + "Maneuver", RequiredCapacity: capacity, Verb: &d.VerbProperties{MeleeDamageDef: capacity, Range: 1.42, Commonality: 1, AccuracyTouch: 1, BurstShotCount: 1}})
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
