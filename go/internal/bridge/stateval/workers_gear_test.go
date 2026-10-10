package stateval

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// rawValue is the worker's GetValueUnfinalized of stat for the subject.
func rawValue(t *testing.T, e *Evaluator, stat string, subject Subject) (float32, error) {
	t.Helper()
	req, err := e.request(stat, subject)
	if err != nil {
		t.Fatal(err)
	}
	return e.unfinalized(req)
}

func wantErr(t *testing.T, err error, substr string) {
	t.Helper()
	if err == nil || !strings.Contains(err.Error(), substr) {
		t.Fatalf("error = %v, want one containing %q", err, substr)
	}
}

func near(a, b float32) bool {
	return math.Abs(float64(a-b)) <= 1e-4*math.Max(1, math.Abs(float64(b)))
}

func TestGearWorkersAreOwned(t *testing.T) {
	e, _ := gearSpec{}.build(t)
	for _, class := range []string{
		"StatWorker_MarketValue", "StatWorker_MeleeArmorPenetration", "StatWorker_MeleeAverageArmorPenetration",
		"StatWorker_MeleeAverageDPS", "StatWorker_MeleeDPS", "StatWorker_PossibleCompOffsets", "StatWorker_ShootingAccuracy",
	} {
		if e.workers[class] == nil {
			t.Errorf("%s is not owned", class)
		}
	}
}

func TestWorkerShootingAccuracyIsTheBaseWorker(t *testing.T) {
	spec := gearSpec{workerClass: "RimWorld.StatWorker_ShootingAccuracy", edit: func(_ *recordedrows.Slice, stat *d.StatDef, _ *d.ThingDef) {
		stat.DefaultBaseValue = 3
	}}
	e, _ := spec.build(t)
	if got, err := e.Value(testStat, ThingSubject("Apparel_Parka", "")); err != nil || got != 3 {
		t.Errorf("= %v, %v; want 3", got, err)
	}
	if shown, err := e.ShouldShowFor(testStat, ThingSubject("Apparel_Parka", "")); err != nil || !shown {
		t.Errorf("shown = %v, %v; want true", shown, err)
	}
}

func TestWorkerPossibleCompOffsets(t *testing.T) {
	spec := gearSpec{workerClass: "RimWorld.StatWorker_PossibleCompOffsets", edit: func(_ *recordedrows.Slice, stat *d.StatDef, _ *d.ThingDef) {
		stat.DefaultBaseValue = 3
	}}
	e, _ := spec.build(t)
	comp := func(c Known[*StatOffsetCompState]) Subject {
		s := ThingSubject("Apparel_Parka", "")
		s.Context = gearCtx(func(g *GearFacts) { g.StatOffsetComp = c })
		return s
	}
	for _, c := range []struct {
		name    string
		subject Subject
		want    float32
		err     string
	}{
		{"definition request", ThingSubject("Apparel_Parka", ""), 3, ""},
		{"no comp", comp(Some((*StatOffsetCompState)(nil))), 3, ""},
		{"a comp offsetting this stat", comp(Some(&StatOffsetCompState{StatDef: testStat, Offset: 4.5})), 7.5, ""},
		{"a comp offsetting another stat", comp(Some(&StatOffsetCompState{StatDef: "Other", Offset: 4.5})), 3, ""},
		{"comp not observed", comp(Known[*StatOffsetCompState]{}), 0, "stat offset comp"},
	} {
		t.Run(c.name, func(t *testing.T) {
			got, err := rawValue(t, e, testStat, c.subject)
			if c.err != "" {
				wantErr(t, err, c.err)
				return
			}
			if err != nil || got != c.want {
				t.Errorf("= %v, %v; want %v", got, err, c.want)
			}
		})
	}
}

// --- StatWorker_MarketValue ---

func TestWorkerMarketValueThingBranches(t *testing.T) {
	spec := marketSpec().withEdit(func(s *recordedrows.Slice, _ *d.StatDef, _ *d.ThingDef) {
		for _, stat := range s.Wire.Defs.StatDefs {
			if stat.DefName == statMarketValue {
				stat.Parts = nil
			}
		}
	})
	spec.things = []string{"Wall", "WoodLog"}
	e, _ := spec.build(t)
	steelValue, err := e.Value(statMarketValue, ThingSubject("Steel", ""))
	if err != nil {
		t.Fatal(err)
	}
	comps := func(c *CompStatTerms) *StatContext {
		return gearCtx(func(g *GearFacts) { g.Comps = Some(c) })
	}
	with := func(def string, ctx *StatContext) Subject {
		s := ThingSubject(def, "")
		s.Context = ctx
		return s
	}
	t.Run("a statBases value", func(t *testing.T) {
		got, err := rawValue(t, e, statMarketValue, with("Steel", comps(nil)))
		if err != nil || got != steelValue {
			t.Errorf("= %v, %v; want %v", got, err, steelValue)
		}
	})
	t.Run("comp offsets then factors", func(t *testing.T) {
		c := &CompStatTerms{
			Offsets: map[string][]float32{statMarketValue: {1, 2}, "Other": {100}},
			Factors: map[string][]float32{statMarketValue: {2, 0.5, 3}, "Other": {100}},
		}
		got, err := rawValue(t, e, statMarketValue, with("Steel", comps(c)))
		want := float32(float32(float32(float32(steelValue+1)+2)*2)*0.5) * 3
		if err != nil || got != want {
			t.Errorf("= %v, %v; want %v", got, err, want)
		}
	})
	t.Run("comps not observed", func(t *testing.T) {
		_, err := rawValue(t, e, statMarketValue, with("Steel", &StatContext{}))
		wantErr(t, err, "thing's comps")
	})
	t.Run("another stat delegates to the market value worker", func(t *testing.T) {
		spec := gearSpec{workerClass: "RimWorld.StatWorker_MarketValue"}
		other, _ := spec.build(t)
		want, err := workerMarketValue{}.Unfinalized(mustRequest(t, other, statMarketValue, ThingSubject("Steel", "")))
		if err != nil {
			t.Fatal(err)
		}
		got, err := rawValue(t, other, testStat, ThingSubject("Steel", ""))
		if err != nil || got != want {
			t.Errorf("= %v, %v; want %v", got, err, want)
		}
	})
	t.Run("a derived value takes the relic's steel", func(t *testing.T) {
		plain, err := rawValue(t, e, statMarketValue, with("Wall", gearCtx(func(g *GearFacts) { g.Comps = Some((*CompStatTerms)(nil)); g.RelicStyle = Some(false) })))
		if err != nil {
			t.Fatal(err)
		}
		stuffed := ThingSubject("Wall", "WoodLog")
		wood, err := rawValue(t, e, statMarketValue, stuffed)
		if err != nil {
			t.Fatal(err)
		}
		steel, err := rawValue(t, e, statMarketValue, ThingSubject("Wall", "Steel"))
		if err != nil {
			t.Fatal(err)
		}
		relic := with("Wall", gearCtx(func(g *GearFacts) { g.Comps = Some((*CompStatTerms)(nil)); g.RelicStyle = Some(true) }))
		got, err := rawValue(t, e, statMarketValue, relic)
		if err != nil || got != steel {
			t.Errorf("relic = %v, %v; want the steel wall %v", got, err, steel)
		}
		notRelic := with("Wall", gearCtx(func(g *GearFacts) { g.Comps = Some((*CompStatTerms)(nil)); g.RelicStyle = Some(false) }))
		if got, err := rawValue(t, e, statMarketValue, notRelic); err != nil || got != plain {
			t.Errorf("not relic = %v, %v; want %v", got, err, plain)
		}
		if wood == steel {
			t.Error("stuffs price the wall the same: the test proves nothing")
		}
	})
	t.Run("relic style not observed", func(t *testing.T) {
		_, err := rawValue(t, e, statMarketValue, with("Wall", comps(nil)))
		wantErr(t, err, "relic")
	})
}

func mustRequest(t *testing.T, e *Evaluator, stat string, subject Subject) *Request {
	t.Helper()
	req, err := e.request(stat, subject)
	if err != nil {
		t.Fatal(err)
	}
	return req
}

func TestWorkerMarketValuePawnPrice(t *testing.T) {
	spec := gearSpec{
		sets: []string{"pawn_capacity_defs", "life_stage_defs", "hediff_defs"},
		edit: func(s *recordedrows.Slice, _ *d.StatDef, _ *d.ThingDef) {
			for _, stat := range s.Wire.Defs.StatDefs {
				if stat.DefName == statMarketValue {
					stat.Parts = nil
				}
			}
			s.Wire.Defs.HediffDefs = append(s.Wire.Defs.HediffDefs,
				&d.HediffDef{DefName: "TestPriced", PriceImpact: true, PriceOffset: 40},
				&d.HediffDef{DefName: "TestSpawns", PriceImpact: true, SpawnThingOnRemoved: "Steel"},
				&d.HediffDef{DefName: "TestSmall", PriceOffset: 0.5},
				&d.HediffDef{DefName: "TestNone"},
			)
		},
	}
	e, _ := spec.build(t)
	game, err := e.catalog.GameConstants()
	if err != nil {
		t.Fatal(err)
	}
	price := game.GetPriceUtility()
	subject := ThingSubject("Apparel_Parka", "")
	base, err := e.baseUnfinalized(mustRequest(t, e, statMarketValue, subject))
	if err != nil {
		t.Fatal(err)
	}
	steel, err := e.baseMarketValue("Steel")
	if err != nil {
		t.Fatal(err)
	}
	var stage *d.LifeStageDef
	for _, row := range e.stub().defs[(&d.LifeStageDef{}).ProtoReflect().Descriptor().FullName()] {
		if r := row.(*d.LifeStageDef); r.GetMarketValueFactor() != 1 && stage == nil {
			stage = r
		}
	}
	if stage == nil {
		t.Fatal("no life stage with a market value factor")
	}
	capacities := map[string]CapacityState{}
	for name := range e.stub().defs[(&d.PawnCapacityDef{}).ProtoReflect().Descriptor().FullName()] {
		capacities[name] = CapacityState{Capable: true, TradeLevel: 1}
	}
	if len(capacities) == 0 {
		t.Fatal("no pawn capacities")
	}
	// healthy is a pawn at full health with every capacity at full trade level.
	healthy := func(edit func(*PawnPriceFacts)) Subject {
		facts := PawnPriceFacts{SummaryHealthPercent: 1, Capacities: map[string]CapacityState{}, LifeStage: stage.GetDefName()}
		for k, v := range capacities {
			facts.Capacities[k] = v
		}
		if edit != nil {
			edit(&facts)
		}
		s := subject
		s.Context = &StatContext{Pawn: &PawnState{}, Gear: GearFacts{PawnPrice: Some(facts)}}
		return s
	}
	lerp := func(a, b, x float32) float32 { return lerp32(a, b, x) }
	someCapacity := func() string {
		for name := range capacities {
			return name
		}
		return ""
	}()
	missing := 1 - price.GetMissingCapacityFactor()
	_ = missing
	stageFactor := stage.GetMarketValueFactor()
	for _, c := range []struct {
		name    string
		subject Subject
		factor  float32 // the quality price factor
		offset  float32
		err     string
	}{
		{"a healthy pawn", healthy(nil), stageFactor, 0, ""},
		{"trait offsets add", healthy(func(p *PawnPriceFacts) { p.TraitValueOffsets = []float32{0.25, 0.25} }), float32(stageFactor + 0.25 + 0.25), 0, ""},
		{"beauty adds a fifth", healthy(func(p *PawnPriceFacts) { p.PawnBeauty = 2 }), float32(stageFactor + float32(2*float32(0.2))), 0, ""},
		{"the factor floors at the minimum", healthy(func(p *PawnPriceFacts) { p.TraitValueOffsets = []float32{-50} }), price.GetMinFactor(), 0, ""},
		{"poor health", healthy(func(p *PawnPriceFacts) { p.SummaryHealthPercent = 0.5 }),
			float32(lerp(1-price.GetSummaryHealthImpact(), 1, 0.5) * stageFactor), 0, ""},
		{"an incapable capacity", healthy(func(p *PawnPriceFacts) { p.Capacities[someCapacity] = CapacityState{Capable: false} }),
			float32(price.GetMissingCapacityFactor() * stageFactor), 0, ""},
		{"a capacity below full trade level", healthy(func(p *PawnPriceFacts) { p.Capacities[someCapacity] = CapacityState{Capable: true, TradeLevel: 0.5} }),
			float32(lerp(1-price.GetCapacityImpact(), 1, 0.5) * stageFactor), 0, ""},
		{"skills at the curve's middle", healthy(func(p *PawnPriceFacts) { p.HasSkills = true; p.SkillLevels = []int32{5, 6} }), stageFactor, 0, ""},
		{"hediff price offsets", healthy(func(p *PawnPriceFacts) { p.Hediffs = []string{"TestPriced", "TestPriced"} }), stageFactor, 80, ""},
		{"a hediff priced by its spawned thing", healthy(func(p *PawnPriceFacts) { p.Hediffs = []string{"TestSpawns"} }), stageFactor, steel, ""},
		{"small offsets and unpriced hediffs add nothing", healthy(func(p *PawnPriceFacts) { p.Hediffs = []string{"TestSmall", "TestNone"} }), stageFactor, 0, ""},
		{"price facts not observed", func() Subject {
			s := subject
			s.Context = &StatContext{Pawn: &PawnState{}}
			return s
		}(), 0, 0, "price facts"},
		{"a capacity not observed", healthy(func(p *PawnPriceFacts) { delete(p.Capacities, someCapacity) }), 0, 0, someCapacity},
		{"a skill tracker with no skills", healthy(func(p *PawnPriceFacts) { p.HasSkills = true }), 0, 0, "no skills"},
		{"an unknown life stage", healthy(func(p *PawnPriceFacts) { p.LifeStage = "NoSuchStage" }), 0, 0, "NoSuchStage"},
		{"an unknown hediff", healthy(func(p *PawnPriceFacts) { p.Hediffs = []string{"NoSuchHediff"} }), 0, 0, "NoSuchHediff"},
	} {
		t.Run(c.name, func(t *testing.T) {
			got, err := rawValue(t, e, statMarketValue, c.subject)
			if c.err != "" {
				wantErr(t, err, c.err)
				return
			}
			want := float32(float32(base*c.factor) + c.offset)
			if err != nil || !near(got, want) {
				t.Errorf("= %v, %v; want %v", got, err, want)
			}
		})
	}
}

// --- melee workers ---

func meleeSpec(worker string) gearSpec {
	return gearSpec{workerClass: worker, edit: func(s *recordedrows.Slice, _ *d.StatDef, _ *d.ThingDef) {
		for _, stat := range s.Wire.Defs.StatDefs {
			if stat.DefName == statMeleeHitChance {
				stat.Parts = nil
			}
		}
	}}
}

func verbs(entries ...MeleeVerbEntry) *StatContext {
	return &StatContext{Pawn: &PawnState{}, Gear: GearFacts{
		Stats:      map[string]float32{statMeleeHitChance: 0.8},
		MeleeVerbs: Some(entries),
	}}
}

func TestWorkerMeleeDPSAndArmorPenetration(t *testing.T) {
	slow := MeleeVerbEntry{IsMeleeAttack: true, SelectionWeight: 2, Damage: 10, ArmorPenetration: 0.3, CooldownTicks: 60}
	fast := MeleeVerbEntry{IsMeleeAttack: true, SelectionWeight: 2, Damage: 20, ArmorPenetration: 0.6, CooldownTicks: 120}
	ranged := MeleeVerbEntry{IsMeleeAttack: false, SelectionWeight: 50, Damage: 99, ArmorPenetration: 9, CooldownTicks: 1}
	idle := MeleeVerbEntry{IsMeleeAttack: true, SelectionWeight: 0, Damage: 7, ArmorPenetration: 7, CooldownTicks: 7}
	notAPawn := &StatContext{Gear: GearFacts{Stats: map[string]float32{statMeleeHitChance: 0.8}}}
	noHitChance := &StatContext{Pawn: &PawnState{}, Gear: GearFacts{MeleeVerbs: Some([]MeleeVerbEntry{slow})}}
	noVerbs := &StatContext{Pawn: &PawnState{}, Gear: GearFacts{Stats: map[string]float32{statMeleeHitChance: 0.8}}}
	hitChance := func(t *testing.T, e *Evaluator) float32 {
		v, err := e.Value(statMeleeHitChance, ThingSubject("Apparel_Parka", ""))
		if err != nil {
			t.Fatal(err)
		}
		return v
	}
	with := func(ctx *StatContext) Subject {
		s := ThingSubject("Apparel_Parka", "")
		s.Context = ctx
		return s
	}

	dps, _ := meleeSpec("RimWorld.StatWorker_MeleeDPS").build(t)
	for _, c := range []struct {
		name string
		ctx  *StatContext
		want float32
		err  string
	}{
		{"weighted damage over weighted cooldown", verbs(slow, fast), 15 * 0.8 / 1.5, ""},
		{"non-melee verbs are skipped", verbs(slow, ranged, fast), 15 * 0.8 / 1.5, ""},
		{"a zero weight adds nothing", verbs(slow, idle), 10 * 0.8 / 1, ""},
		{"no verbs", verbs(), 0, ""},
		{"only weightless verbs", verbs(idle), 0, ""},
		{"only ranged verbs", verbs(ranged), 0, ""},
		{"a thing that is not a pawn", notAPawn, 0, ""},
		{"verbs not observed", noVerbs, 0, "melee verbs"},
		{"hit chance not observed", noHitChance, 0, statMeleeHitChance},
	} {
		t.Run("DPS "+c.name, func(t *testing.T) {
			got, err := rawValue(t, dps, testStat, with(c.ctx))
			if c.err != "" {
				wantErr(t, err, c.err)
				return
			}
			if err != nil || !near(got, c.want) {
				t.Errorf("= %v, %v; want %v", got, err, c.want)
			}
		})
	}
	t.Run("DPS definition request", func(t *testing.T) {
		// No pawn: damage 0 over cooldown 1, times the def's hit chance.
		got, err := rawValue(t, dps, testStat, ThingSubject("Apparel_Parka", ""))
		if err != nil || got != 0*hitChance(t, dps) {
			t.Errorf("= %v, %v; want 0", got, err)
		}
	})

	ap, _ := meleeSpec("RimWorld.StatWorker_MeleeArmorPenetration").build(t)
	for _, c := range []struct {
		name string
		ctx  *StatContext
		want float32
		err  string
	}{
		{"weighted armor penetration", verbs(slow, fast), 0.45, ""},
		{"non-melee verbs are skipped", verbs(slow, ranged, fast), 0.45, ""},
		{"no verbs", verbs(), 0, ""},
		{"only weightless verbs", verbs(idle), 0, ""},
		{"a thing that is not a pawn", notAPawn, 0, ""},
		{"verbs not observed", noVerbs, 0, "melee verbs"},
	} {
		t.Run("AP "+c.name, func(t *testing.T) {
			got, err := rawValue(t, ap, testStat, with(c.ctx))
			if c.err != "" {
				wantErr(t, err, c.err)
				return
			}
			if err != nil || !near(got, c.want) {
				t.Errorf("= %v, %v; want %v", got, err, c.want)
			}
		})
	}
	if got, err := rawValue(t, ap, testStat, ThingSubject("Apparel_Parka", "")); err != nil || got != 0 {
		t.Errorf("AP definition request = %v, %v; want 0", got, err)
	}

	// Both are shown for a pawn thing whose base visibility holds, and for
	// nothing else.
	for name, w := range map[string]showWorker{"DPS": workerMeleeDPS{}, "AP": workerMeleeArmorPenetration{}} {
		for _, c := range []struct {
			name string
			ctx  *StatContext
			base bool
			want bool
		}{
			{"a pawn", verbs(), true, true},
			{"a pawn the base hides", verbs(), false, false},
			{"a thing that is not a pawn", notAPawn, true, false},
			{"a definition", nil, true, false},
		} {
			t.Run(name+" shown for "+c.name, func(t *testing.T) {
				req := mustRequest(t, dps, testStat, with(c.ctx))
				got, err := w.Show(req, func() (bool, error) { return c.base, nil })
				if err != nil || got != c.want {
					t.Errorf("= %v, %v; want %v", got, err, c.want)
				}
			})
		}
		t.Run(name+" base error", func(t *testing.T) {
			_, err := w.Show(mustRequest(t, dps, testStat, with(verbs())), func() (bool, error) { return false, errBase })
			if err != errBase {
				t.Errorf("err = %v", err)
			}
		})
	}
}

var errBase = &baseError{}

type baseError struct{}

func (*baseError) Error() string { return "base" }

// meleeWeaponSpec is a catalog holding the wooden club, the maneuvers and what
// they read.
func meleeWeaponSpec(worker string, edit func(club *d.ThingDef)) gearSpec {
	return gearSpec{
		things:      []string{"MeleeWeapon_Club", "WoodLog"},
		sets:        []string{"maneuver_defs", "tool_capacity_defs", "damage_defs", "damage_armor_category_defs"},
		workerClass: worker,
		edit: func(s *recordedrows.Slice, _ *d.StatDef, _ *d.ThingDef) {
			for _, stat := range s.Wire.Defs.StatDefs {
				if stat.DefName == statMeleeWeaponDamageMult || stat.DefName == statMeleeWeaponCooldownMult {
					stat.Parts = nil
				}
			}
			if edit != nil {
				edit(s.Thing("MeleeWeapon_Club"))
			}
		},
	}
}

// clubSources are the club's melee verbs: each tool's maneuvers, in order.
func clubSources(t *testing.T, e *Evaluator) []verbSource {
	t.Helper()
	club := e.catalog.ThingDef("MeleeWeapon_Club")
	sources, err := e.meleeSources(club.GetVerbs(), club.GetTools())
	if err != nil || len(sources) == 0 {
		t.Fatalf("club sources = %d, %v", len(sources), err)
	}
	return sources
}

func TestWorkerMeleeAverageWeapons(t *testing.T) {
	spec := meleeWeaponSpec("RimWorld.StatWorker_MeleeAverageDPS", nil)
	e, _ := spec.build(t)
	sources := clubSources(t, e)

	// The club's figures, from its own rows: one definition request through
	// the stat value, then a thing with stated multipliers.
	damageMult, cooldownMult := float32(1.5), float32(0.5)
	thing := func(stuff string, wielder Known[*WielderState]) Subject {
		s := ThingSubject("MeleeWeapon_Club", stuff)
		s.Context = &StatContext{Gear: GearFacts{
			Stats:   map[string]float32{statMeleeWeaponDamageMult: damageMult, statMeleeWeaponCooldownMult: cooldownMult},
			Wielder: wielder,
		}}
		return s
	}
	average := func(each func(verbSource) float32, weight func(verbSource) float32) float32 {
		var num, sum float64
		for _, s := range sources {
			w := float64(weight(s))
			num += w
			sum += w * float64(each(s))
		}
		return float32(sum / num)
	}
	damage := func(s verbSource) float32 { return s.tool.GetPower() * damageMult }
	weight := func(s verbSource) float32 {
		return damage(s) * damage(s) * s.verb.GetCommonality() * s.tool.GetChanceFactor()
	}
	cooldown := func(s verbSource) float32 { return s.tool.GetCooldownTime() * cooldownMult }
	armor := func(s verbSource) float32 {
		if s.tool.GetArmorPenetration() < 0 {
			return damage(s) * 0.015
		}
		return s.tool.GetArmorPenetration() * damageMult
	}

	t.Run("a held-by-nobody thing", func(t *testing.T) {
		got, err := rawValue(t, e, "MeleeWeapon_AverageDPS", thing("", Some((*WielderState)(nil))))
		want := average(damage, weight) / average(cooldown, weight)
		if err != nil || !near(got, want) {
			t.Errorf("DPS = %v, %v; want %v", got, err, want)
		}
	})
	t.Run("the wielder is not observed", func(t *testing.T) {
		_, err := rawValue(t, e, "MeleeWeapon_AverageDPS", thing("", Known[*WielderState]{}))
		wantErr(t, err, "holding the weapon")
	})
	t.Run("a wielder scales damage and cooldown", func(t *testing.T) {
		w := &WielderState{
			Intelligence: d.Intelligence_INTELLIGENCE_HUMANLIKE, LifeStageMeleeDamageFactor: 0.5,
			Stats: map[string]float32{statMeleeDamageFactor: 2, statMeleeCooldownFactor: 0.25},
		}
		got, err := rawValue(t, e, "MeleeWeapon_AverageDPS", thing("", Some(w)))
		scaled := func(f func(verbSource) float32, by float32) func(verbSource) float32 {
			return func(s verbSource) float32 { return f(s) * by }
		}
		sw := func(s verbSource) float32 {
			return scaled(damage, 1)(s) * scaled(damage, 1)(s) * s.verb.GetCommonality() * s.tool.GetChanceFactor()
		}
		_ = sw
		// Damage scales by 0.5 * 2 = 1 and cooldown by 0.25; the weights follow the damage.
		want := average(damage, weight) / average(scaled(cooldown, 0.25), weight)
		if err != nil || !near(got, want) {
			t.Errorf("DPS = %v, %v; want %v", got, err, want)
		}
	})
	t.Run("a wielder's missing stat", func(t *testing.T) {
		w := &WielderState{Intelligence: d.Intelligence_INTELLIGENCE_HUMANLIKE, LifeStageMeleeDamageFactor: 1}
		_, err := rawValue(t, e, "MeleeWeapon_AverageDPS", thing("", Some(w)))
		wantErr(t, err, statMeleeDamageFactor)
	})
	t.Run("armor penetration of a thing", func(t *testing.T) {
		ap, _ := meleeWeaponSpec("RimWorld.StatWorker_MeleeAverageArmorPenetration", nil).build(t)
		got, err := rawValue(t, ap, "MeleeWeapon_AverageArmorPenetration", thing("", Some((*WielderState)(nil))))
		want := average(armor, weight)
		if err != nil || !near(got, want) {
			t.Errorf("AP = %v, %v; want %v", got, err, want)
		}
	})
	t.Run("a stuffed thing multiplies by the stuff's damage factor", func(t *testing.T) {
		plain, err := rawValue(t, e, "MeleeWeapon_AverageDPS", thing("", Some((*WielderState)(nil))))
		if err != nil {
			t.Fatal(err)
		}
		wood, err := rawValue(t, e, "MeleeWeapon_AverageDPS", thing("WoodLog", Some((*WielderState)(nil))))
		if err != nil {
			t.Fatal(err)
		}
		if wood == plain || wood <= 0 {
			t.Errorf("stuffed %v, plain %v: the stuff's armor category multiplier did not apply", wood, plain)
		}
	})
	t.Run("a definition request through the stat value", func(t *testing.T) {
		got, err := e.Value("MeleeWeapon_AverageDPS", ThingSubject("MeleeWeapon_Club", ""))
		var damages, cooldowns, weights float32
		for _, s := range sources {
			w := s.tool.GetPower() * s.tool.GetPower() * s.verb.GetCommonality() * s.tool.GetChanceFactor()
			weights += w
			damages += s.tool.GetPower() * w
			cooldowns += s.tool.GetCooldownTime() * w
		}
		want := (damages / weights) / (cooldowns / weights)
		if err != nil || !near(got, want) {
			t.Errorf("DPS = %v, %v; want %v", got, err, want)
		}
	})
	t.Run("a terrain is no weapon", func(t *testing.T) {
		ts := e.stub().terrains
		for name := range ts {
			got, err := rawValue(t, e, "MeleeWeapon_AverageDPS", TerrainSubject(name))
			if err != nil || got != 0 {
				t.Errorf("terrain %s = %v, %v; want 0", name, got, err)
			}
			break
		}
	})
}

func TestWorkerMeleeAverageBodyPartGroup(t *testing.T) {
	// Give the club a tool linked to a body part group: the wielder's natural
	// efficiency of that group scales the damage, floored at 0.4 when the
	// group must always be usable.
	for _, c := range []struct {
		name       string
		ensure     bool
		efficiency Known[float32]
		scale      float32
		err        string
	}{
		{"efficiency scales damage", false, Some(float32(0.5)), 0.5, ""},
		{"an always-usable group floors at 0.4", true, Some(float32(0.1)), 0.4, ""},
		{"an always-usable group above the floor", true, Some(float32(0.9)), 0.9, ""},
		{"efficiency not observed", false, Known[float32]{}, 0, "Hands"},
	} {
		t.Run(c.name, func(t *testing.T) {
			linked := meleeWeaponSpec("RimWorld.StatWorker_MeleeAverageArmorPenetration", func(club *d.ThingDef) {
				for _, tool := range club.Tools {
					tool.Value.LinkedBodyPartsGroup = "Hands"
					tool.Value.EnsureLinkedBodyPartsGroupAlwaysUsable = c.ensure
					tool.Value.ArmorPenetration = -1
				}
			})
			e, _ := linked.build(t)
			sources := clubSources(t, e)
			efficiency := map[string]float32{}
			if c.efficiency.OK {
				efficiency["Hands"] = c.efficiency.V
			}
			wielder := &WielderState{
				Intelligence: d.Intelligence_INTELLIGENCE_HUMANLIKE, LifeStageMeleeDamageFactor: 1,
				Stats: map[string]float32{statMeleeDamageFactor: 1}, PartEfficiency: efficiency,
			}
			s := ThingSubject("MeleeWeapon_Club", "")
			s.Context = &StatContext{Gear: GearFacts{
				Stats:   map[string]float32{statMeleeWeaponDamageMult: 1},
				Wielder: Some(wielder),
			}}
			got, err := rawValue(t, e, "MeleeWeapon_AverageArmorPenetration", s)
			if c.err != "" {
				wantErr(t, err, c.err)
				return
			}
			// Armor penetration of a tool without its own is 1.5% of the damage; the
			// weights are damage squared, so scaling the damage scales the average.
			var num, sum float64
			for _, src := range sources {
				dmg := float64(src.tool.GetPower() * c.scale)
				w := dmg * dmg * float64(src.verb.GetCommonality()) * float64(src.tool.GetChanceFactor())
				num += w
				sum += w * dmg * 0.015
			}
			if want := float32(sum / num); err != nil || !near(got, want) {
				t.Errorf("AP = %v, %v; want %v", got, err, want)
			}
		})
	}
}

func TestWorkerMeleeAverageIntelligenceGate(t *testing.T) {
	// A wielder less intelligent than the verb allows leaves it no weight: with
	// every verb gated, the average is 0/0.
	gated := meleeWeaponSpec("RimWorld.StatWorker_MeleeAverageArmorPenetration", func(club *d.ThingDef) {})
	e, _ := gated.build(t)
	for _, s := range clubSources(t, e) {
		s.verb.MinIntelligence = d.Intelligence_INTELLIGENCE_HUMANLIKE
	}
	// The maneuver rows are shared by every tool using them; the gate is on the verb rows.
	s := ThingSubject("MeleeWeapon_Club", "")
	s.Context = &StatContext{Gear: GearFacts{
		Stats: map[string]float32{statMeleeWeaponDamageMult: 1},
		Wielder: Some(&WielderState{
			Intelligence: d.Intelligence_INTELLIGENCE_ANIMAL, LifeStageMeleeDamageFactor: 1,
			Stats: map[string]float32{statMeleeDamageFactor: 1},
		}),
	}}
	got, err := rawValue(t, e, "MeleeWeapon_AverageArmorPenetration", s)
	if err != nil || !math.IsNaN(float64(got)) {
		t.Errorf("= %v, %v; want NaN", got, err)
	}
}
