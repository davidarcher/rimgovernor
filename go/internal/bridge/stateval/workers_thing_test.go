package stateval

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// wrapComp is the Opt_CompPropertiesAny entry holding row, whatever its class.
func wrapComp(t *testing.T, row proto.Message) *d.Opt_CompPropertiesAny {
	t.Helper()
	any := &d.CompPropertiesAny{}
	msg := any.ProtoReflect()
	fields := msg.Descriptor().Oneofs().ByName("value").Fields()
	for i := 0; i < fields.Len(); i++ {
		if f := fields.Get(i); f.Message().FullName() == row.ProtoReflect().Descriptor().FullName() {
			msg.Set(f, protoreflect.ValueOfMessage(row.ProtoReflect()))
			return &d.Opt_CompPropertiesAny{Value: any}
		}
	}
	t.Fatalf("no CompPropertiesAny member for %T", row)
	return nil
}

func studiable(requires bool) *d.CompProperties_Studiable {
	return &d.CompProperties_Studiable{CompClass: "RimWorld.CompStudiable", RequiresHoldingPlatform: requires}
}

func holdingTarget(kind string, cold bool) *d.CompProperties_HoldingPlatformTarget {
	return &d.CompProperties_HoldingPlatformTarget{CompClass: "RimWorld.CompHoldingPlatformTarget", HeldPawnKind: kind, GetsColdContainmentBonus: cold}
}

// workerRig is a rig whose stat runs the worker class and whose parka has comps.
func workerRig(t *testing.T, sets []string, worker string, edit func(parka *d.ThingDef)) *rig {
	t.Helper()
	return newThingRig(t, sets, func(stat *d.StatDef, parka, _ *d.ThingDef) {
		stat.WorkerClass = "RimWorld." + worker
		parka.Comps = nil
		parka.Building = &d.BuildingProperties{}
		if edit != nil {
			edit(parka)
		}
	})
}

func (r *rig) request(subject Subject) *Request {
	r.t.Helper()
	req, err := r.eval.request(testStat, subject)
	if err != nil {
		r.t.Fatal(err)
	}
	return req
}

func withContext(ctx *StatContext) Subject {
	s := ThingSubject("Apparel_Parka", "")
	s.Context = ctx
	return s
}

func TestWorkerContainmentShow(t *testing.T) {
	comps := func(rows ...proto.Message) func(*d.ThingDef) {
		return func(def *d.ThingDef) {
			for _, row := range rows {
				def.Comps = append(def.Comps, wrapComp(t, row))
			}
		}
	}
	pawn := func(mutant Known[bool]) *StatContext {
		return factsCtx(func(c *StatContext) { c.Pawn = &PawnState{}; c.Thing.PawnIsMutant = mutant })
	}
	for _, c := range []struct {
		name string
		edit func(*d.ThingDef)
		ctx  *StatContext
		want bool
		err  string
	}{
		{"definition request", comps(studiable(true), holdingTarget("Pig", false)), nil, false, ""},
		{"studiable pawn that needs a platform", comps(studiable(true)), pawn(Some(false)), true, ""},
		{"studiable pawn that does not", comps(studiable(false)), pawn(Some(false)), false, ""},
		{"a mutant needs a platform", comps(studiable(false)), pawn(Some(true)), true, ""},
		{"a studiable pawn skips the held kind", comps(studiable(false), holdingTarget("Pig", false)), pawn(Some(false)), false, ""},
		{"mutant not observed", comps(studiable(false)), pawn(Known[bool]{}), false, "mutant"},
		{"a thing that holds a pawn kind", comps(holdingTarget("Pig", false)), &StatContext{}, true, ""},
		{"studiable non-pawn falls to the target", comps(studiable(true), holdingTarget("Pig", false)), &StatContext{}, true, ""},
		{"no held kind", comps(holdingTarget("", false)), &StatContext{}, false, ""},
		{"no comps", nil, &StatContext{}, false, ""},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := workerRig(t, nil, "StatWorker_MinimumContainmentStrength", c.edit)
			req := r.request(withContext(c.ctx))
			if c.ctx == nil {
				req = r.request(ThingSubject("Apparel_Parka", ""))
			}
			got, err := workerMinimumContainmentStrength{}.Show(req, nil)
			if c.err != "" {
				if err == nil || !strings.Contains(err.Error(), c.err) {
					t.Fatalf("error = %v, want %q", err, c.err)
				}
				return
			}
			if err != nil || got != c.want {
				t.Errorf("= %v, %v; want %v", got, err, c.want)
			}
		})
	}
}

func TestWorkerColdContainmentBonus(t *testing.T) {
	cold := func(def *d.ThingDef) { def.Comps = append(def.Comps, wrapComp(t, holdingTarget("Pig", true))) }
	warm := func(def *d.ThingDef) { def.Comps = append(def.Comps, wrapComp(t, holdingTarget("Pig", false))) }
	at := func(temp float32) *StatContext {
		return factsCtx(func(c *StatContext) { c.Thing.AmbientTemperature = Some(temp) })
	}
	for _, c := range []struct {
		name      string
		edit      func(*d.ThingDef)
		ctx       *StatContext
		show      bool
		want      float32
		err       string
		showError bool
	}{
		{"definition request", cold, nil, false, -1, "", false},
		{"cold at -15", cold, at(-15), true, 0.25, "", false},
		{"cold at -30", cold, at(-30), true, 0.5, "", false},
		{"cold above freezing", cold, at(10), true, 0, "", false},
		{"no cold bonus", warm, at(-15), false, -1, "", false},
		{"no comp", nil, at(-15), false, -1, "", false},
		{"temperature not observed", cold, &StatContext{}, false, 0, "ambient temperature", true},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := workerRig(t, nil, "StatWorker_ColdContainmentBonus", c.edit)
			subject := ThingSubject("Apparel_Parka", "")
			if c.ctx != nil {
				subject = withContext(c.ctx)
			}
			req := r.request(subject)
			w := workerColdContainmentBonus{}
			got, err := w.Unfinalized(req)
			if c.err != "" {
				if err == nil || !strings.Contains(err.Error(), c.err) {
					t.Fatalf("error = %v, want %q", err, c.err)
				}
				return
			}
			if err != nil || got != c.want {
				t.Errorf("value = %v, %v; want %v", got, err, c.want)
			}
			shown, err := w.Show(req, nil)
			// The comps above have no studiable, so the held pawn kind is what shows.
			if err != nil || shown != (c.show && c.ctx != nil) {
				t.Errorf("shown = %v, %v; want %v", shown, err, c.show)
			}
		})
	}
}

func TestWorkerContainmentStrength(t *testing.T) {
	holder := func(factor float32) func(*d.ThingDef) {
		return func(def *d.ThingDef) {
			def.Comps = append(def.Comps, wrapComp(t, &d.CompProperties_EntityHolderPlatform{CompClass: "RimWorld.CompEntityHolderPlatform", ContainmentFactor: factor}))
		}
	}
	t.Run("show", func(t *testing.T) {
		for _, c := range []struct {
			name string
			edit func(*d.ThingDef)
			want bool
		}{
			{"an entity holder", holder(1), true},
			{"a stat base", func(def *d.ThingDef) { def.StatBases = append(def.StatBases, mod("ContainmentStrength", 3)) }, true},
			{"a zero stat base", func(def *d.ThingDef) { def.StatBases = append(def.StatBases, mod("ContainmentStrength", 0)) }, false},
			{"neither", nil, false},
		} {
			r := workerRig(t, nil, "StatWorker_ContainmentStrength", c.edit)
			got, err := workerContainmentStrength{}.Show(r.request(ThingSubject("Apparel_Parka", "")), nil)
			if err != nil || got != c.want {
				t.Errorf("%s: shown = %v, %v; want %v", c.name, got, err, c.want)
			}
		}
	})
	room := func(edit func(*ContainmentRoom)) *StatContext {
		rm := &ContainmentRoom{CellGlows: []float32{0.5, 1}, CellCount: 2}
		edit(rm)
		return factsCtx(func(c *StatContext) { c.Thing.Containment = Some(rm) })
	}
	full := func(rm *ContainmentRoom) {
		rm.BorderEdificeHitPoints = []int32{1000, 1000}
		rm.Doors = []ContainmentDoor{{HitPoints: 500}, {HitPoints: 500}}
		rm.OpenCellTerrains = []string{"$terrain"}
		rm.OtherHolders = 1
		rm.OpenRoofCount = 1
	}
	// lighting 7.5 + walls 100 + doors 100 + floor 4, one other platform
	// (x0.9 of 211.5 is 190.35), not fully roofed -30, holder factor 2.
	const fullSum = (7.5 + 100 + 100 + 4 + (211.5*0.9 - 211.5) - 30) * 2
	for _, c := range []struct {
		name string
		ctx  *StatContext
		edit func(*d.ThingDef)
		want float64 // added to the base value of 1
		err  string
	}{
		{"definition request", nil, holder(2), 0, ""},
		{"no room", factsCtx(func(c *StatContext) { c.Thing.Containment = Some[*ContainmentRoom](nil) }), holder(2), 0, ""},
		{"a full room", room(full), holder(2), fullSum, ""},
		{"no holder comp is a factor of one", room(func(rm *ContainmentRoom) { rm.OpenRoofCount = 0 }), nil, 7.5, ""},
		{"a breached door zeroes the door term", room(func(rm *ContainmentRoom) {
			rm.Doors = []ContainmentDoor{{HitPoints: 500}, {HitPoints: 500, ContainmentBreach: true}, {HitPoints: 500}}
		}), nil, 7.5, ""},
		{"an outdoor room scores nothing", room(func(rm *ContainmentRoom) { rm.PsychologicallyOutdoors = true; full(rm) }), holder(2), 0, ""},
		{"not observed", &StatContext{}, nil, 0, "containment room"},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := workerRig(t, nil, "StatWorker_ContainmentStrength", func(def *d.ThingDef) {
				def.StatBases = append(def.StatBases, mod(testStat, 1))
				if c.edit != nil {
					c.edit(def)
				}
			})
			for _, terrain := range r.eval.catalog.TerrainDefs {
				terrain.StatBases = append(terrain.StatBases, mod("ContainmentStrength", 4))
			}
			subject := ThingSubject("Apparel_Parka", "")
			if c.ctx != nil {
				ctx := *c.ctx
				if rm := ctx.Thing.Containment.V; ctx.Thing.Containment.OK && rm != nil {
					copied := *rm
					copied.OpenCellTerrains = nil
					for _, name := range rm.OpenCellTerrains {
						if name == "$terrain" {
							name = anyTerrain(r)
						}
						copied.OpenCellTerrains = append(copied.OpenCellTerrains, name)
					}
					ctx.Thing.Containment = Some(&copied)
				}
				subject = withContext(&ctx)
			}
			got, err := workerContainmentStrength{}.Unfinalized(r.request(subject))
			if c.err != "" {
				if err == nil || !strings.Contains(err.Error(), c.err) {
					t.Fatalf("error = %v, want %q", err, c.err)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if want := 1 + c.want; math.Abs(float64(got)-want) > 1e-3 {
				t.Errorf("= %v, want %v", got, want)
			}
		})
	}
}

func TestWorkerMeleeDamageAmountTrap(t *testing.T) {
	trap := func(def *d.ThingDef) {
		def.Category = d.ThingCategory_THING_CATEGORY_BUILDING
		def.Building = &d.BuildingProperties{IsTrap: true, TrapDamageCategory: "Sharp"}
		def.StatBases = append(def.StatBases, mod(testStat, 10))
	}
	t.Run("value", func(t *testing.T) {
		r := workerRig(t, []string{"damage_armor_category_defs"}, "StatWorker_MeleeDamageAmountTrap", trap)
		if got, err := r.eval.Value(testStat, ThingSubject("Apparel_Parka", "")); err != nil || got != 2 {
			t.Errorf("no stuff = %v, %v; want 10/5", got, err)
		}
		row := bridge.DefRow[*d.DamageArmorCategoryDef](r.eval.catalog, "Sharp")
		if row == nil {
			t.Fatal("no Sharp damage armor category")
		}
		mult, err := r.eval.Value(row.GetMultStat(), ThingSubject("Steel", ""))
		if err != nil {
			t.Fatal(err)
		}
		want := float32(float32(10*mult) / 5)
		if got, err := r.eval.Value(testStat, ThingSubject("Apparel_Parka", "Steel")); err != nil || got != want {
			t.Errorf("steel = %v, %v; want %v (multiplier %v)", got, err, want, mult)
		}
		if _, err := r.eval.Value(testStat, TerrainSubject(anyTerrain(r))); err == nil {
			t.Error("a terrain def was evaluated")
		}
	})
	t.Run("no trap category", func(t *testing.T) {
		r := workerRig(t, []string{"damage_armor_category_defs"}, "StatWorker_MeleeDamageAmountTrap", func(def *d.ThingDef) {
			trap(def)
			def.Building.TrapDamageCategory = ""
		})
		if got, err := r.eval.Value(testStat, ThingSubject("Apparel_Parka", "Steel")); err != nil || got != 2 {
			t.Errorf("= %v, %v; want 10/5", got, err)
		}
	})
	t.Run("show", func(t *testing.T) {
		shown := func(edit func(*d.ThingDef), ctx *StatContext, terrain bool) (bool, error) {
			r := workerRig(t, nil, "StatWorker_MeleeDamageAmountTrap", edit)
			subject := ThingSubject("Apparel_Parka", "")
			if terrain {
				subject = TerrainSubject(anyTerrain(r))
			}
			if ctx != nil {
				subject = withContext(ctx)
			}
			return workerMeleeDamageAmountTrap{}.Show(r.request(subject), nil)
		}
		known := func(v bool) *StatContext {
			return factsCtx(func(c *StatContext) { c.Thing.ShowTrapDamageStat = Some(v) })
		}
		damager := func(def *d.ThingDef) { trap(def); def.ThingClass = "RimWorld.Building_TrapDamager" }
		for _, c := range []struct {
			name    string
			edit    func(*d.ThingDef)
			ctx     *StatContext
			terrain bool
			want    bool
			err     string
		}{
			{"a trap def", trap, nil, false, true, ""},
			{"a building that is no trap", func(def *d.ThingDef) { trap(def); def.Building.IsTrap = false }, nil, false, false, ""},
			{"not a building", func(def *d.ThingDef) { trap(def); def.Category = d.ThingCategory_THING_CATEGORY_ITEM }, nil, false, false, ""},
			{"a terrain", trap, nil, true, false, ""},
			{"a trap that shows its damage", damager, known(true), false, true, ""},
			{"a trap that hides its damage", damager, known(false), false, false, ""},
			{"a trap whose flag is not observed", damager, &StatContext{}, false, false, "ShouldShowTrapDamageStat"},
			{"a non-trap class ignores the flag", trap, &StatContext{}, false, true, ""},
		} {
			got, err := shown(c.edit, c.ctx, c.terrain)
			if c.err != "" {
				if err == nil || !strings.Contains(err.Error(), c.err) {
					t.Errorf("%s: error = %v, want %q", c.name, err, c.err)
				}
				continue
			}
			if err != nil || got != c.want {
				t.Errorf("%s: shown = %v, %v; want %v", c.name, got, err, c.want)
			}
		}
	})
}

func anyTerrain(r *rig) string {
	for name := range r.eval.catalog.TerrainDefs {
		return name
	}
	return ""
}

func TestWorkerShowByClass(t *testing.T) {
	for _, c := range []struct {
		worker string
		w      showWorker
		class  string
		other  string
	}{
		{"StatWorker_RoomReadingBonus", workerRoomReadingBonus{}, "RimWorld.Building_Bookcase", "RimWorld.Building_Bed"},
		{"StatWorker_SurgerySuccessChanceFactor", workerSurgerySuccessChanceFactor{}, "RimWorld.Building_Bed", "RimWorld.Building_Bookcase"},
	} {
		t.Run(c.worker, func(t *testing.T) {
			yes := func() (bool, error) { return true, nil }
			no := func() (bool, error) { return false, nil }
			for _, k := range []struct {
				name    string
				class   string
				base    func() (bool, error)
				terrain bool
				want    bool
			}{
				{"the class", c.class, yes, false, true},
				{"another class", c.other, yes, false, false},
				{"hidden by the base", c.class, no, false, false},
				{"a terrain", c.class, yes, true, false},
			} {
				r := workerRig(t, nil, c.worker, asClass(k.class))
				subject := ThingSubject("Apparel_Parka", "")
				if k.terrain {
					subject = TerrainSubject(anyTerrain(r))
				}
				got, err := c.w.Show(r.request(subject), k.base)
				if err != nil || got != k.want {
					t.Errorf("%s: %v, %v; want %v", k.name, got, err, k.want)
				}
			}
		})
	}
}

func TestWorkerMaxPowerOutput(t *testing.T) {
	plant := func(consumption float32, upgrades ...*d.CompProperties_Power_PowerUpgrade) func(*d.ThingDef) {
		return func(def *d.ThingDef) {
			power := &d.CompProperties_Power{CompClass: "RimWorld.CompPowerPlantSolar", BasePowerConsumption: consumption}
			for _, up := range upgrades {
				power.PowerUpgrades = append(power.PowerUpgrades, &d.Opt_CompProperties_Power_PowerUpgrade{Value: up})
			}
			def.Comps = append(def.Comps, wrapComp(t, power))
		}
	}
	upgrade := &d.CompProperties_Power_PowerUpgrade{ResearchProject: "Electricity", Factor: 0.5}
	research := func(done ...string) *StatContext {
		set := map[string]bool{}
		for _, name := range done {
			set[name] = true
		}
		return factsCtx(func(c *StatContext) { c.Thing.ResearchFinished = Some(set) })
	}
	for _, c := range []struct {
		name string
		edit func(*d.ThingDef)
		ctx  *StatContext
		want float32
		err  string
	}{
		{"a power plant", plant(-1700), nil, 1700, ""},
		{"a consumer", plant(100), nil, -100, ""},
		{"no power plant comp", nil, nil, 0, ""},
		{"an unfinished upgrade", plant(-1000, upgrade), research(), 1000, ""},
		{"a finished upgrade", plant(-1000, upgrade), research("Electricity"), 500, ""},
		{"an upgrade needs the research state", plant(-1000, upgrade), nil, 0, "research"},
		{"research not observed", plant(-1000, upgrade), &StatContext{}, 0, "research"},
		{"zero consumption", plant(0), nil, 0, ""},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := workerRig(t, nil, "StatWorker_MaxPowerOutput", c.edit)
			subject := ThingSubject("Apparel_Parka", "")
			if c.ctx != nil {
				subject = withContext(c.ctx)
			}
			got, err := workerMaxPowerOutput{}.Unfinalized(r.request(subject))
			if c.err != "" {
				if err == nil || !strings.Contains(err.Error(), c.err) {
					t.Fatalf("error = %v, want %q", err, c.err)
				}
				return
			}
			if err != nil || got != c.want || math.Signbit(float64(got)) != math.Signbit(float64(c.want)) {
				t.Errorf("= %v, %v; want %v", got, err, c.want)
			}
		})
	}
	r := workerRig(t, nil, "StatWorker_MaxPowerOutput", plant(-1700))
	if got, err := (workerMaxPowerOutput{}).Unfinalized(r.request(TerrainSubject(anyTerrain(r)))); err != nil || got != 0 {
		t.Errorf("a terrain = %v, %v; the game logs and answers 0", got, err)
	}
}

func TestDisplayOnlyWorkersAreRegistered(t *testing.T) {
	owned := ownedWorkers()
	for _, w := range thingWorkerList() {
		if owned[w.Class()] == nil {
			t.Errorf("%s is not registered", w.Class())
		}
	}
	if len(thingWorkerList()) != 9 {
		t.Errorf("%d thing workers, want 9", len(thingWorkerList()))
	}
	for _, class := range []string{"StatWorker_MechEnergyLossPerHP", "StatWorker_PsyfocusCost"} {
		w := owned[class]
		if _, ok := w.(unfinalizedWorker); ok {
			t.Errorf("%s overrides the value", class)
		}
		if _, ok := w.(showWorker); ok {
			t.Errorf("%s overrides ShouldShowFor", class)
		}
		if _, ok := w.(baseValueWorker); ok {
			t.Errorf("%s overrides the base value", class)
		}
	}
}
