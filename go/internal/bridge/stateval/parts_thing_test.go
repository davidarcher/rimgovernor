package stateval

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// newThingRig is newRig with more recorded def sets in the catalog.
func newThingRig(t *testing.T, sets []string, edit func(stat *d.StatDef, parka, steel *d.ThingDef)) *rig {
	t.Helper()
	slice := recordedrows.Take(t, recordedrows.Named("Apparel_Parka", "Steel"),
		append([]string{"stat_defs", "stat_category_defs", "flesh_type_defs"}, sets...)...)
	stat := &d.StatDef{
		DefName: testStat, Category: "BasicsNonPawn", WorkerClass: "RimWorld.StatWorker",
		ShowOnUntradeables: true, ShowOnUnhaulables: true, ShowIfUndefined: true,
		ApplyFactorsIfNegative: true, MinValue: -1e6, MaxValue: 1e6, RoundToFiveOver: 3.4028235e38,
	}
	var parka, steel *d.ThingDef
	for _, row := range slice.Wire.ThingDefs {
		switch row.DefName {
		case "Apparel_Parka":
			parka = row
		case "Steel":
			steel = row
		}
	}
	edit(stat, parka, steel)
	slice.Wire.Defs.StatDefs = append(slice.Wire.Defs.StatDefs, stat)
	catalog, err := recordedcatalog.FromSlice(slice, "unit")
	if err != nil {
		t.Fatal(err)
	}
	env := Env{ActiveMods: map[string]bool{"ludeon.rimworld": true}, ScenarioFactors: map[string]float32{}}
	return &rig{t: t, stat: stat, eval: New(catalog, env)}
}

// thingCase is one part over one thing request. edit shapes the parka def,
// terrain the catalog's one terrain (a fact naming "$terrain" is that terrain).
type thingCase struct {
	name    string
	row     proto.Message
	edit    func(*d.ThingDef)
	terrain func(*d.TerrainDef)
	ctx     *StatContext
	biotech bool
	want    float32
	err     string
}

func factsCtx(edit func(*StatContext)) *StatContext {
	c := &StatContext{}
	edit(c)
	return c
}

func runThingCases(t *testing.T, cases []thingCase) {
	t.Helper()
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := newThingRig(t, nil, func(stat *d.StatDef, parka, _ *d.ThingDef) {
				stat.Parts = []*d.Opt_StatPartAny{wrapPart(t, c.row)}
				parka.DeteriorateFromEnvironmentalEffects, parka.UseHitPoints, parka.HealthAffectsPrice = false, false, false
				if c.edit != nil {
					c.edit(parka)
				}
			})
			if c.biotech {
				r.eval.env.ActiveMods["ludeon.rimworld.biotech"] = true
			}
			var terrainName string
			for name, terrain := range r.eval.catalog.TerrainDefs {
				terrainName = name
				if c.terrain != nil {
					c.terrain(terrain)
				}
			}
			subject := ThingSubject("Apparel_Parka", "")
			if c.ctx != nil {
				ctx := *c.ctx
				if ctx.Thing.Terrain.V == "$terrain" {
					ctx.Thing.Terrain = Some(terrainName)
				}
				subject.Context = &ctx
			}
			req, err := r.eval.request(testStat, subject)
			if err != nil {
				t.Fatal(err)
			}
			got, err := r.eval.finalize(req, 10)
			if c.err != "" {
				if err == nil || !strings.Contains(err.Error(), c.err) {
					t.Fatalf("error = %v, want one containing %q", err, c.err)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if got != c.want {
				t.Errorf("= %v, want %v", got, c.want)
			}
		})
	}
}

func asClass(class string) func(*d.ThingDef) {
	return func(def *d.ThingDef) { def.ThingClass = class }
}

func deteriorates(def *d.ThingDef) { def.DeteriorateFromEnvironmentalEffects = true }

func f32(v float32) *float32 { return &v }

func TestThingPartContentsBeauty(t *testing.T) {
	row := &d.StatPart_ContentsBeauty{}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, want: 10},
		{name: "a beauty container", row: row, ctx: factsCtx(func(c *StatContext) { c.Thing.BeautyOffset = Some(f32(2.5)) }), want: 12.5},
		{name: "a negative offset", row: row, ctx: factsCtx(func(c *StatContext) { c.Thing.BeautyOffset = Some(f32(-4)) }), want: 6},
		{name: "not a container", row: row, ctx: factsCtx(func(c *StatContext) { c.Thing.BeautyOffset = Some[*float32](nil) }), want: 10},
		{name: "not observed", row: row, ctx: &StatContext{}, err: "BeautyOffset"},
	})
}

func TestThingPartCorpseCasket(t *testing.T) {
	row := &d.StatPart_CorpseCasket{OffsetOccupied: 5}
	casket := asClass("RimWorld.Building_Sarcophagus")
	has := func(v bool) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.CorpseCasketHasCorpse = Some(v) })
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, edit: casket, want: 10},
		{name: "occupied", row: row, edit: casket, ctx: has(true), want: 15},
		{name: "empty", row: row, edit: casket, ctx: has(false), want: 10},
		{name: "not a casket, fact never read", row: row, ctx: &StatContext{}, want: 10},
		{name: "zero offset", row: &d.StatPart_CorpseCasket{}, edit: casket, ctx: has(true), want: 10},
		{name: "listed def", row: &d.StatPart_CorpseCasket{OffsetOccupied: -3, ThingDefs: []string{"Apparel_Parka"}}, edit: casket, ctx: has(true), want: 7},
		{name: "other listed def", row: &d.StatPart_CorpseCasket{OffsetOccupied: 5, ThingDefs: []string{"Steel"}}, edit: casket, ctx: has(true), want: 10},
		{name: "not observed", row: row, edit: casket, ctx: &StatContext{}, err: "corpse casket"},
	})
}

func TestThingPartEnvironmentalEffects(t *testing.T) {
	row := &d.StatPart_EnvironmentalEffects{FactorOffsetUnroofed: 0.5, FactorOffsetOutdoors: 1, ProtectedByEdificeFactor: 0.5}
	env := func(edit func(*StatContext)) *StatContext {
		return factsCtx(func(c *StatContext) {
			c.Spawned, c.Roofed = Some(true), Some(true)
			c.Thing.RoomUsesOutdoorTemperature = Some(false)
			c.Thing.Terrain = Some("")
			c.Thing.RainRate = Some(float32(0))
			c.Thing.ProtectedByEdifice = Some(false)
			edit(c)
		})
	}
	extra := func(td *d.TerrainDef) { td.ExtraDeteriorationFactor = 0.25 }
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, edit: deteriorates, want: 10},
		{name: "unspawned", row: row, edit: deteriorates, ctx: factsCtx(func(c *StatContext) { c.Spawned = Some(false) }), want: 10},
		{name: "does not deteriorate", row: row, ctx: factsCtx(func(c *StatContext) { c.Spawned = Some(true) }), want: 10},
		{name: "roofed and indoors is nothing", row: row, edit: deteriorates, ctx: env(func(*StatContext) {}), want: 0},
		{name: "unroofed", row: row, edit: deteriorates, ctx: env(func(c *StatContext) { c.Roofed = Some(false) }), want: 5},
		{name: "outdoor room", row: row, edit: deteriorates, ctx: env(func(c *StatContext) { c.Thing.RoomUsesOutdoorTemperature = Some(true) }), want: 10},
		{name: "terrain, outdoors, unroofed in rain", row: row, edit: deteriorates, terrain: extra, ctx: env(func(c *StatContext) {
			c.Roofed, c.Thing.RoomUsesOutdoorTemperature, c.Thing.Terrain, c.Thing.RainRate = Some(false), Some(true), Some("$terrain"), Some(float32(0.5))
		}), want: 52.5},
		{name: "rain is ignored under a roof", row: row, edit: deteriorates, ctx: env(func(c *StatContext) {
			c.Thing.RoomUsesOutdoorTemperature, c.Thing.RainRate = Some(true), Some(float32(1))
		}), want: 10},
		{name: "terrain with no extra deterioration", row: row, edit: deteriorates, ctx: env(func(c *StatContext) {
			c.Thing.RoomUsesOutdoorTemperature, c.Thing.Terrain = Some(true), Some("$terrain")
		}), want: 10},
		{name: "protected by an edifice", row: row, edit: deteriorates, ctx: env(func(c *StatContext) {
			c.Thing.RoomUsesOutdoorTemperature, c.Thing.ProtectedByEdifice = Some(true), Some(true)
		}), want: 5},
		{name: "roof not observed", row: row, edit: deteriorates, ctx: factsCtx(func(c *StatContext) { c.Spawned = Some(true) }), err: "roofed"},
		{name: "room not observed", row: row, edit: deteriorates, ctx: factsCtx(func(c *StatContext) {
			c.Spawned, c.Roofed = Some(true), Some(true)
		}), err: "outdoor temperature"},
		{name: "rain not observed", row: row, edit: deteriorates, ctx: env(func(c *StatContext) {
			c.Roofed, c.Thing.RainRate = Some(false), Known[float32]{}
		}), err: "rain rate"},
		{name: "edifice not observed", row: row, edit: deteriorates, ctx: env(func(c *StatContext) {
			c.Thing.ProtectedByEdifice = Known[bool]{}
		}), err: "edifice"},
		{name: "terrain not observed", row: row, edit: deteriorates, ctx: env(func(c *StatContext) {
			c.Thing.Terrain = Known[string]{}
		}), err: "terrain"},
	})
}

func TestThingPartNearHarbingerTree(t *testing.T) {
	row := &d.StatPart_NearHarbingerTree{Multiplier: 3}
	being := func(v bool) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.HarbingerBeingConsumed = Some(v) })
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, want: 10},
		{name: "consumed", row: row, ctx: being(true), want: 30},
		{name: "not consumed", row: row, ctx: being(false), want: 10},
		{name: "not observed", row: row, ctx: &StatContext{}, err: "harbinger"},
	})
}

func TestThingPartNoxiousHaze(t *testing.T) {
	row := &d.StatPart_NoxiousHaze{Multiplier: 0.5}
	item := func(def *d.ThingDef) { deteriorates(def); def.Category = d.ThingCategory_THING_CATEGORY_ITEM }
	building := func(def *d.ThingDef) { deteriorates(def); def.Category = d.ThingCategory_THING_CATEGORY_BUILDING }
	hazy := func(edit func(*StatContext)) *StatContext {
		return factsCtx(func(c *StatContext) {
			n := &c.Thing.NoxiousHaze
			n.SpawnedOrAnyParentSpawned, n.Active, n.HeldRoofed = Some(true), Some(true), Some(false)
			edit(c)
		})
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, biotech: true, edit: item, want: 10},
		{name: "no Biotech", row: row, edit: item, ctx: hazy(func(*StatContext) {}), want: 10},
		{name: "does not deteriorate", row: row, biotech: true, ctx: hazy(func(*StatContext) {}), want: 10},
		{name: "unroofed item", row: row, biotech: true, edit: item, ctx: hazy(func(*StatContext) {}), want: 5},
		{name: "roofed item", row: row, biotech: true, edit: item, ctx: hazy(func(c *StatContext) { c.Thing.NoxiousHaze.HeldRoofed = Some(true) }), want: 10},
		{name: "not spawned anywhere", row: row, biotech: true, edit: item, ctx: hazy(func(c *StatContext) { c.Thing.NoxiousHaze.SpawnedOrAnyParentSpawned = Some(false) }), want: 10},
		{name: "no active haze", row: row, biotech: true, edit: item, ctx: hazy(func(c *StatContext) { c.Thing.NoxiousHaze.Active = Some(false) }), want: 10},
		{name: "unroofed building", row: row, biotech: true, edit: building, ctx: hazy(func(*StatContext) {}), want: 5},
		{name: "roofed building in a psychologically outdoor room", row: row, biotech: true, edit: building, ctx: hazy(func(c *StatContext) {
			c.Thing.NoxiousHaze.HeldRoofed, c.Thing.NoxiousHaze.HeldRoomPsychologicallyOutdoors = Some(true), Some(true)
		}), want: 5},
		{name: "roofed building indoors", row: row, biotech: true, edit: building, ctx: hazy(func(c *StatContext) {
			c.Thing.NoxiousHaze.HeldRoofed, c.Thing.NoxiousHaze.HeldRoomPsychologicallyOutdoors = Some(true), Some(false)
		}), want: 10},
		{name: "immune pawn", row: row, biotech: true, edit: building, ctx: hazy(func(c *StatContext) {
			c.Pawn = &PawnState{}
			c.Thing.NoxiousHaze.PawnImmune = Some(true)
		}), want: 10},
		{name: "susceptible pawn", row: row, biotech: true, edit: building, ctx: hazy(func(c *StatContext) {
			c.Pawn = &PawnState{}
			c.Thing.NoxiousHaze.PawnImmune = Some(false)
		}), want: 5},
		{name: "pawn immunity not observed", row: row, biotech: true, edit: building, ctx: hazy(func(c *StatContext) { c.Pawn = &PawnState{} }), err: "immune"},
		{name: "spawned not observed", row: row, biotech: true, edit: item, ctx: &StatContext{}, err: "spawned"},
		{name: "haze not observed", row: row, biotech: true, edit: item, ctx: hazy(func(c *StatContext) { c.Thing.NoxiousHaze.Active = Known[bool]{} }), err: "noxious haze"},
		{name: "room not observed", row: row, biotech: true, edit: building, ctx: hazy(func(c *StatContext) { c.Thing.NoxiousHaze.HeldRoofed = Some(true) }), err: "psychologically outdoors"},
	})
}

func TestThingPartPollution(t *testing.T) {
	row := &d.StatPart_Pollution{Multiplier: 0.25}
	cell := func(spawned, polluted bool) *StatContext {
		return factsCtx(func(c *StatContext) { c.Spawned, c.Thing.Polluted = Some(spawned), Some(polluted) })
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, biotech: true, edit: deteriorates, want: 10},
		{name: "polluted", row: row, biotech: true, edit: deteriorates, ctx: cell(true, true), want: 2.5},
		{name: "clean", row: row, biotech: true, edit: deteriorates, ctx: cell(true, false), want: 10},
		{name: "unspawned", row: row, biotech: true, edit: deteriorates, ctx: cell(false, true), want: 10},
		{name: "does not deteriorate", row: row, biotech: true, ctx: cell(true, true), want: 10},
		{name: "no Biotech", row: row, edit: deteriorates, ctx: cell(true, true), want: 10},
		{name: "not observed", row: row, biotech: true, edit: deteriorates, ctx: factsCtx(func(c *StatContext) { c.Spawned = Some(true) }), err: "polluted"},
	})
}

func TestThingPartToxicFallout(t *testing.T) {
	row := &d.StatPart_ToxicFallout{Multiplier: 0.5}
	fallout := func(edit func(*ToxicFalloutFacts)) *StatContext {
		return factsCtx(func(c *StatContext) {
			f := &c.Thing.ToxicFallout
			f.HasMapHeld, f.Active, f.PositionHeldValid, f.PositionHeldRoofed = Some(true), Some(true), Some(true), Some(false)
			edit(f)
		})
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, edit: deteriorates, want: 10},
		{name: "exposed", row: row, edit: deteriorates, ctx: fallout(func(*ToxicFalloutFacts) {}), want: 5},
		{name: "does not deteriorate", row: row, ctx: fallout(func(*ToxicFalloutFacts) {}), want: 10},
		{name: "no map", row: row, edit: deteriorates, ctx: fallout(func(f *ToxicFalloutFacts) { f.HasMapHeld = Some(false) }), want: 10},
		{name: "no fallout", row: row, edit: deteriorates, ctx: fallout(func(f *ToxicFalloutFacts) { f.Active = Some(false) }), want: 10},
		{name: "invalid position", row: row, edit: deteriorates, ctx: fallout(func(f *ToxicFalloutFacts) { f.PositionHeldValid = Some(false) }), want: 10},
		{name: "roofed", row: row, edit: deteriorates, ctx: fallout(func(f *ToxicFalloutFacts) { f.PositionHeldRoofed = Some(true) }), want: 10},
		{name: "map not observed", row: row, edit: deteriorates, ctx: &StatContext{}, err: "on a map"},
		{name: "condition not observed", row: row, edit: deteriorates, ctx: fallout(func(f *ToxicFalloutFacts) { f.Active = Known[bool]{} }), err: "toxic fallout"},
		{name: "position not observed", row: row, edit: deteriorates, ctx: fallout(func(f *ToxicFalloutFacts) { f.PositionHeldValid = Known[bool]{} }), err: "held position"},
		{name: "roof not observed", row: row, edit: deteriorates, ctx: fallout(func(f *ToxicFalloutFacts) { f.PositionHeldRoofed = Known[bool]{} }), err: "roofed"},
	})
}

func TestThingPartHasRelic(t *testing.T) {
	row := &d.StatPart_HasRelic{Offset: 4}
	held := func(v bool) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.RelicContained = Some(v) })
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, want: 10},
		{name: "holds a relic", row: row, ctx: held(true), want: 14},
		{name: "empty", row: row, ctx: held(false), want: 10},
		{name: "not observed", row: row, ctx: &StatContext{}, err: "relic"},
	})
}

func TestThingPartHealth(t *testing.T) {
	row := &d.StatPart_Health{Curve: curve(0, 0, 1, 1)}
	hp := func(hp, max int32) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.HitPoints, c.Thing.MaxHitPoints = Some(hp), Some(max) })
	}
	priced := func(def *d.ThingDef) { def.UseHitPoints, def.HealthAffectsPrice = true, true }
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, edit: priced, want: 10},
		{name: "half health", row: row, edit: priced, ctx: hp(50, 100), want: 5},
		{name: "full health", row: row, edit: priced, ctx: hp(100, 100), want: 10},
		{name: "health does not affect price", row: row, edit: func(def *d.ThingDef) { def.UseHitPoints = true }, ctx: hp(50, 100), want: 10},
		{name: "no hit points", row: row, edit: func(def *d.ThingDef) { def.HealthAffectsPrice = true }, ctx: hp(50, 100), want: 10},
		{name: "hit points not observed", row: row, edit: priced, ctx: &StatContext{}, err: "hit points"},
		{name: "max hit points not observed", row: row, edit: priced, ctx: factsCtx(func(c *StatContext) { c.Thing.HitPoints = Some(int32(5)) }), err: "max hit points"},
	})
}

func TestThingPartRot(t *testing.T) {
	rot := func(stage string) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.Rot = Some(stage) })
	}
	corpse := asClass("Verse.Corpse")
	fresh := &d.StatPart_IsCorpseFresh{}
	runThingCases(t, []thingCase{
		{name: "fresh corpse", row: fresh, edit: corpse, ctx: rot(ThingRotFresh), want: 10},
		{name: "rotting corpse", row: fresh, edit: corpse, ctx: rot(ThingRotRotting), want: 0},
		{name: "dessicated corpse", row: fresh, edit: corpse, ctx: rot(ThingRotDessicated), want: 0},
		{name: "not a corpse", row: fresh, ctx: rot(ThingRotRotting), want: 10},
		{name: "definition request", row: fresh, edit: corpse, want: 10},
		{name: "corpse rot not observed", row: fresh, edit: corpse, ctx: &StatContext{}, err: "rot stage"},
	})
	max := &d.StatPart_MaxChanceIfRotting{}
	runThingCases(t, []thingCase{
		{name: "max chance, fresh", row: max, ctx: rot(ThingRotFresh), want: 10},
		{name: "max chance, rotting", row: max, ctx: rot(ThingRotRotting), want: 1},
		{name: "max chance, dessicated", row: max, ctx: rot(ThingRotDessicated), want: 1},
		{name: "max chance, definition request", row: max, want: 10},
		{name: "max chance, not observed", row: max, ctx: &StatContext{}, err: "rot stage"},
	})
}

func TestThingPartPlantGrowthNutritionFactor(t *testing.T) {
	row := &d.StatPart_PlantGrowthNutritionFactor{}
	grown := func(g float32) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.PlantGrowth = Some(g) })
	}
	sown := func(def *d.ThingDef) {
		def.ThingClass = "RimWorld.Plant"
		def.Plant = &d.PlantProperties{SowTags: []string{"Ground"}}
	}
	wild := func(def *d.ThingDef) {
		def.ThingClass = "RimWorld.Plant"
		def.Plant = &d.PlantProperties{}
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, edit: sown, want: 10},
		{name: "sowable plant", row: row, edit: sown, ctx: grown(0.5), want: 5},
		{name: "wild plant", row: row, edit: wild, ctx: grown(0.5), want: 7.5},
		{name: "wild plant at full growth", row: row, edit: wild, ctx: grown(1), want: 10},
		{name: "not a plant", row: row, ctx: grown(0.5), want: 10},
		{name: "growth not observed", row: row, edit: sown, ctx: &StatContext{}, err: "growth"},
		{name: "plant without properties", row: row, edit: asClass("RimWorld.Plant"), ctx: grown(0.5), err: "plant properties"},
	})
}

func TestThingPartWorkTableUnpowered(t *testing.T) {
	row := &d.StatPart_WorkTableUnpowered{}
	table := func(factor float32) func(*d.ThingDef) {
		return func(def *d.ThingDef) { def.Building = &d.BuildingProperties{UnpoweredWorkTableWorkSpeedFactor: factor} }
	}
	off := func(v bool) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.PowerTraderOff = Some(v) })
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, edit: table(0.5), want: 10},
		{name: "unpowered", row: row, edit: table(0.5), ctx: off(true), want: 5},
		{name: "powered or no trader", row: row, edit: table(0.5), ctx: off(false), want: 10},
		{name: "no unpowered penalty", row: row, edit: table(0), ctx: &StatContext{}, want: 10},
		{name: "power not observed", row: row, edit: table(0.5), ctx: &StatContext{}, err: "CompPowerTrader"},
		{name: "not a building", row: row, edit: func(def *d.ThingDef) { def.Building = nil }, ctx: off(true), err: "building properties"},
	})
}

func TestThingPartArtificialBuildingsNearbyOffset(t *testing.T) {
	row := &d.StatPart_ArtificialBuildingsNearbyOffset{Curve: curve(0, 0, 4, 1), Radius: 10}
	near := func(spawned bool, counts map[float32]int32) *StatContext {
		return factsCtx(func(c *StatContext) {
			c.Spawned, c.Thing.ArtificialBuildingsNear = Some(spawned), Some(counts)
		})
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, want: 10},
		{name: "two nearby", row: row, ctx: near(true, map[float32]int32{10: 2}), want: 10.5},
		{name: "none nearby", row: row, ctx: near(true, map[float32]int32{10: 0}), want: 10},
		{name: "many nearby", row: row, ctx: near(true, map[float32]int32{10: 40}), want: 11},
		{name: "unspawned", row: row, ctx: near(false, nil), want: 10},
		{name: "other radius", row: row, ctx: near(true, map[float32]int32{5: 2}), err: "radius 10"},
		{name: "spawned not observed", row: row, ctx: &StatContext{}, err: "spawned"},
		{name: "count not observed", row: row, ctx: factsCtx(func(c *StatContext) { c.Spawned = Some(true) }), err: "artificial buildings"},
	})
}

func TestThingPartBiocoded(t *testing.T) {
	row := &d.StatPart_Biocoded{}
	coded := func(v bool) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.Biocoded = Some(v) })
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, want: 10},
		{name: "biocoded", row: row, ctx: coded(true), want: 0},
		{name: "not biocoded", row: row, ctx: coded(false), want: 10},
		{name: "not observed", row: row, ctx: &StatContext{}, err: "biocoded"},
	})
}

func TestThingPartUnfinishedThingIngredientsMass(t *testing.T) {
	row := &d.StatPart_UnfinishedThingIngredientsMass{}
	unfinished := asClass("Verse.UnfinishedThing")
	ingredients := func(ing ...IngredientMass) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.UnfinishedIngredients = Some(ing) })
	}
	runThingCases(t, []thingCase{
		{name: "definition request", row: row, edit: unfinished, want: 10},
		{name: "ingredients", row: row, edit: unfinished, ctx: ingredients(IngredientMass{Mass: 2, StackCount: 3}, IngredientMass{Mass: 1.5, StackCount: 2}), want: 19},
		{name: "no ingredients", row: row, edit: unfinished, ctx: ingredients(), want: 10},
		{name: "not an unfinished thing", row: row, ctx: ingredients(IngredientMass{Mass: 2, StackCount: 3}), want: 10},
		{name: "not observed", row: row, edit: unfinished, ctx: &StatContext{}, err: "ingredients"},
	})
}

// TestThingPartsAreRegistered: every class this group ports is owned and
// keeps ForceShow false.
func TestThingPartsAreRegistered(t *testing.T) {
	owned := ownedParts()
	for _, p := range thingPartList() {
		if owned[p.Class()] == nil {
			t.Errorf("%s is not registered", p.Class())
		}
		if force, err := p.ForceShow(nil, nil); force || err != nil {
			t.Errorf("%s ForceShow = %v, %v", p.Class(), force, err)
		}
	}
	if len(thingPartList()) != 16 {
		t.Errorf("%d thing parts, want 16", len(thingPartList()))
	}
}
