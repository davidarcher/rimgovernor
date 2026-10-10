package stateval

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// gearSpec shapes one small catalog for the gear-group classes: the synthetic
// stat testStat, Apparel_Parka and Steel, a weapon trait "TestTrait" (market
// value offset 30, equipped TestStat offset 2) and what the test adds.
type gearSpec struct {
	things      []string
	sets        []string
	workerClass string
	parts       []proto.Message
	edit        func(s *recordedrows.Slice, stat *d.StatDef, parka *d.ThingDef)
	env         func(*Env)
}

func (g gearSpec) build(t *testing.T) (*Evaluator, *d.StatDef) {
	t.Helper()
	sets := append([]string{"stat_defs", "stat_category_defs", "flesh_type_defs"}, g.sets...)
	slice := recordedrows.Take(t, recordedrows.Named(append([]string{"Apparel_Parka", "Steel"}, g.things...)...), sets...)
	stat := &d.StatDef{
		DefName: testStat, Category: "BasicsNonPawn", WorkerClass: "RimWorld.StatWorker",
		ShowOnUntradeables: true, ShowOnUnhaulables: true, ShowIfUndefined: true,
		ApplyFactorsIfNegative: true, MinValue: -1e6, MaxValue: 1e6, RoundToFiveOver: 3.4028235e38,
	}
	if g.workerClass != "" {
		stat.WorkerClass = g.workerClass
	}
	for _, row := range g.parts {
		stat.Parts = append(stat.Parts, wrapPart(t, row))
	}
	var parka *d.ThingDef
	for _, row := range slice.Wire.ThingDefs {
		if row.DefName == "Apparel_Parka" {
			parka = row
		}
	}
	slice.Wire.Defs.WeaponTraitDefs = append(slice.Wire.Defs.WeaponTraitDefs, &d.WeaponTraitDef{
		DefName: "TestTrait", MarketValueOffset: 30, EquippedStatOffsets: []*d.Opt_StatModifier{mod(testStat, 2)},
	})
	if g.edit != nil {
		g.edit(slice, stat, parka)
	}
	slice.Wire.Defs.StatDefs = append(slice.Wire.Defs.StatDefs, stat)
	catalog := fromWire(slice.Wire)
	env := Env{
		ActiveMods: map[string]bool{"ludeon.rimworld": true}, ScenarioFactors: map[string]float32{},
		Difficulty: Some(Difficulty{ButcherYieldFactor: 0.5, FishingYieldFactor: 2, Flags: map[string]bool{"classicMortars": true, "off": false}}),
	}
	if g.env != nil {
		g.env(&env)
	}
	return New(catalog, env), stat
}

// gearCase is one spec over one context, run through the stat's finalize (the
// parts in priority order) from 10. ctx nil is a definition request of the parka.
type gearCase struct {
	name string
	spec gearSpec
	ctx  *StatContext
	want float32
	err  string // substring of the expected error
}

func runGearCases(t *testing.T, cases []gearCase) {
	t.Helper()
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			e, _ := c.spec.build(t)
			subject := ThingSubject("Apparel_Parka", "")
			subject.Context = c.ctx
			req, err := e.request(testStat, subject)
			if err != nil {
				t.Fatal(err)
			}
			got, err := e.finalize(req, 10)
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

func parts(rows ...proto.Message) gearSpec { return gearSpec{parts: rows} }

func (g gearSpec) withEnv(edit func(*Env)) gearSpec { g.env = edit; return g }

func (g gearSpec) withEdit(edit func(*recordedrows.Slice, *d.StatDef, *d.ThingDef)) gearSpec {
	g.edit = edit
	return g
}

func gearPawn(edit func(*GearFacts)) *StatContext {
	ctx := &StatContext{Pawn: &PawnState{}}
	edit(&ctx.Gear)
	return ctx
}

func gearCtx(edit func(*GearFacts)) *StatContext {
	ctx := &StatContext{}
	edit(&ctx.Gear)
	return ctx
}

func TestGearPartsDifficulty(t *testing.T) {
	noStoryteller := func(e *Env) { e.Difficulty = Some(Difficulty{NoStoryteller: true}) }
	unstated := func(e *Env) { e.Difficulty = Known[Difficulty]{} }
	plain := &d.StatPart_Difficulty{}
	butcher := &d.StatPart_Difficulty_ButcherYield{}
	fishing := &d.StatPart_Difficulty_FishingYield{}
	mining := &d.StatPart_Difficulty_MiningYield{}
	runGearCases(t, []gearCase{
		{name: "obsolete difficulty part multiplies by 1", spec: parts(plain), want: 10},
		{name: "obsolete difficulty part throws without a storyteller", spec: parts(plain).withEnv(noStoryteller), err: "no storyteller"},
		{name: "obsolete difficulty part needs the difficulty", spec: parts(plain).withEnv(unstated), err: "difficulty"},
		{name: "butcher factor", spec: parts(butcher), want: 5},
		{name: "butcher factor on a thing", spec: parts(butcher), ctx: &StatContext{}, want: 5},
		{name: "butcher factor without a storyteller is 1", spec: parts(butcher).withEnv(noStoryteller), want: 10},
		{name: "butcher needs the difficulty", spec: parts(butcher).withEnv(unstated), err: "difficulty"},
		{name: "fishing factor", spec: parts(fishing), want: 20},
		{name: "fishing factor without a storyteller is 1", spec: parts(fishing).withEnv(noStoryteller), want: 10},
		{name: "fishing needs the difficulty", spec: parts(fishing).withEnv(unstated), err: "difficulty"},
		{name: "mining yield part does nothing", spec: parts(mining), want: 10},
		{name: "mining yield part reads no difficulty", spec: parts(mining).withEnv(unstated), want: 10},
	})
}

func TestCostListApplies(t *testing.T) {
	e, _ := gearSpec{}.build(t)
	list := func(variable string, invert bool) *d.CostListForDifficulty {
		return &d.CostListForDifficulty{DifficultyVar: variable, Invert: invert}
	}
	for _, c := range []struct {
		name string
		list *d.CostListForDifficulty
		want bool
		err  string
	}{
		{"no list", nil, false, ""},
		{"setting on", list("classicMortars", false), true, ""},
		{"setting on inverted", list("classicMortars", true), false, ""},
		{"setting off", list("off", false), false, ""},
		{"setting off inverted", list("off", true), true, ""},
		{"no variable", list("", false), false, ""},
		{"unknown setting", list("nope", false), false, "nope"},
	} {
		t.Run(c.name, func(t *testing.T) {
			got, err := e.costListApplies(c.list)
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
	e.env.Difficulty = Some(Difficulty{NoStoryteller: true})
	if got, err := e.costListApplies(list("classicMortars", false)); err != nil || got {
		t.Errorf("no storyteller = %v, %v; want false", got, err)
	}
	e.env.Difficulty = Known[Difficulty]{}
	if _, err := e.costListApplies(list("classicMortars", false)); err == nil {
		t.Error("an unstated difficulty is not an error")
	}
	if got, err := e.costListApplies(nil); err != nil || got {
		t.Errorf("no list with no difficulty = %v, %v; want false", got, err)
	}
}

func TestGearPartGearAndInventoryMass(t *testing.T) {
	mass := &d.StatPart_GearAndInventoryMass{}
	asPawnDef := gearSpec{}.withEdit(func(_ *recordedrows.Slice, _ *d.StatDef, parka *d.ThingDef) {
		parka.Category = d.ThingCategory_THING_CATEGORY_PAWN
	})
	asCorpse := func(ingestible bool) func(*recordedrows.Slice, *d.StatDef, *d.ThingDef) {
		return func(_ *recordedrows.Slice, _ *d.StatDef, parka *d.ThingDef) {
			parka.ThingClass = "Verse.Corpse"
			if ingestible {
				parka.Ingestible = &d.IngestibleProperties{}
			}
		}
	}
	forMass := func(spec gearSpec) gearSpec { spec.parts = []proto.Message{mass}; return spec }
	stacks := MassFacts{
		Apparel: []float32{1.5, 2}, Equipment: []float32{3.5},
		Inventory: []StackMass{{Count: 3, Mass: 0.25}, {Count: 2, Mass: 1}},
	}
	runGearCases(t, []gearCase{
		{name: "a non-pawn def adds nothing", spec: forMass(gearSpec{}), want: 10},
		{name: "a pawn def adds 0", spec: forMass(asPawnDef), want: 10},
		{name: "a corpse def adds 0", spec: forMass(gearSpec{edit: asCorpse(true)}), want: 10},
		{name: "a corpse def without an ingestible throws", spec: forMass(gearSpec{edit: asCorpse(false)}), err: "no ingestible"},
		{name: "a pawn sums gear and inventory", spec: forMass(gearSpec{}), ctx: gearPawn(func(g *GearFacts) { g.Mass = Some(stacks) }), want: 10 + (1.5 + 2 + 3.5) + (0.75 + 2)},
		{name: "a pawn with nothing carried", spec: forMass(gearSpec{}), ctx: gearPawn(func(g *GearFacts) { g.Mass = Some(MassFacts{}) }), want: 10},
		{name: "a pawn's mass not observed", spec: forMass(gearSpec{}), ctx: gearPawn(func(*GearFacts) {}), err: "gear and inventory mass"},
		{name: "a corpse thing sums its pawn's gear", spec: forMass(gearSpec{edit: asCorpse(true)}), ctx: gearCtx(func(g *GearFacts) { g.Mass = Some(stacks) }), want: 10 + 7 + 2.75},
		{name: "a corpse thing's mass not observed", spec: forMass(gearSpec{edit: asCorpse(true)}), ctx: &StatContext{}, err: "gear and inventory mass"},
		{name: "another thing adds nothing", spec: forMass(gearSpec{}), ctx: &StatContext{}, want: 10},
	})

	forceShow := func(ctx *StatContext) (bool, error) {
		e, _ := forMass(gearSpec{}).build(t)
		subject := ThingSubject("Apparel_Parka", "")
		subject.Context = ctx
		req, err := e.request(testStat, subject)
		if err != nil {
			t.Fatal(err)
		}
		return partGearAndInventoryMass{}.ForceShow(req, mass)
	}
	for _, c := range []struct {
		name string
		ctx  *StatContext
		want bool
		err  string
	}{
		{"definition request", nil, false, ""},
		{"pawn", gearPawn(func(*GearFacts) {}), true, ""},
		{"non-pawn thing for a pawn", gearCtx(func(g *GearFacts) { g.ForPawn = Some(true) }), true, ""},
		{"non-pawn thing for no pawn", gearCtx(func(g *GearFacts) { g.ForPawn = Some(false) }), false, ""},
		{"request pawn not observed", &StatContext{}, false, "carries a pawn"},
	} {
		t.Run("force show "+c.name, func(t *testing.T) {
			got, err := forceShow(c.ctx)
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

func gear(def string, stat float32) GearItem {
	return GearItem{Def: def, Stats: map[string]float32{testStat: stat}, Bladelink: Some([]string(nil))}
}

func TestGearPartGearStatFactor(t *testing.T) {
	factor := &d.StatPart_GearStatFactor{ApparelStat: testStat}
	withWeapon := &d.StatPart_GearStatFactor{ApparelStat: testStat, IncludeWeapon: true}
	worn := func(g *GearFacts) { g.Apparel = Some([]GearItem{gear("Apparel_Parka", 2), gear("Apparel_Parka", 3)}) }
	armed := func(weapon *GearItem) func(*GearFacts) {
		return func(g *GearFacts) { worn(g); g.Primary = Some(weapon) }
	}
	rifle := gear("Steel", 0.5)
	missingStat := GearItem{Def: "Apparel_Parka", Stats: map[string]float32{}}
	runGearCases(t, []gearCase{
		{name: "definition request", spec: parts(factor), want: 10},
		{name: "a non-pawn thing", spec: parts(factor), ctx: &StatContext{}, want: 10},
		{name: "worn apparel multiplies", spec: parts(factor), ctx: gearPawn(worn), want: 60},
		{name: "no apparel", spec: parts(factor), ctx: gearPawn(func(g *GearFacts) { g.Apparel = Some([]GearItem(nil)) }), want: 10},
		{name: "the weapon is ignored without includeWeapon", spec: parts(factor), ctx: gearPawn(armed(&rifle)), want: 60},
		{name: "includeWeapon multiplies the primary", spec: parts(withWeapon), ctx: gearPawn(armed(&rifle)), want: 30},
		{name: "includeWeapon with no primary", spec: parts(withWeapon), ctx: gearPawn(armed(nil)), want: 60},
		{name: "apparel not observed", spec: parts(factor), ctx: gearPawn(func(*GearFacts) {}), err: "worn apparel"},
		{name: "primary not observed", spec: parts(withWeapon), ctx: gearPawn(worn), err: "primary weapon"},
		{name: "an apparel's stat not observed", spec: parts(factor), ctx: gearPawn(func(g *GearFacts) { g.Apparel = Some([]GearItem{missingStat}) }), err: testStat},
		{name: "the primary's stat not observed", spec: parts(withWeapon), ctx: gearPawn(armed(&missingStat)), err: testStat},
	})
}

func TestGearPartGearStatOffset(t *testing.T) {
	offset := &d.StatPart_GearStatOffset{ApparelStat: testStat}
	subtract := &d.StatPart_GearStatOffset{ApparelStat: testStat, Subtract: true}
	withWeapon := &d.StatPart_GearStatOffset{ApparelStat: testStat, IncludeWeapon: true}
	worn := func(items ...GearItem) func(*GearFacts) {
		return func(g *GearFacts) { g.Apparel = Some(items) }
	}
	rifle := gear("Steel", 5)
	traited := gear("Apparel_Parka", 4)
	traited.Bladelink = Some([]string{"TestTrait"})
	untraitable := gear("Apparel_Parka", 4)
	untraitable.Bladelink = Known[[]string]{}
	badTrait := gear("Apparel_Parka", 4)
	badTrait.Bladelink = Some([]string{"NoSuchTrait"})
	// The stat's own parts run over the gear, as a thing request of the gear:
	// the trait's equipped offset 2 becomes 2 + 30 through the weapon trait part.
	recursing := parts(offset, &d.StatPart_WeaponTraitsMarketValueOffset{})
	traitedWithFacts := traited
	traitedWithFacts.Context = gearCtx(func(g *GearFacts) { g.Bladelink = Some([]string{"TestTrait"}) })
	noFacts := func(g *GearFacts) { worn(gear("Apparel_Parka", 4))(g); g.Bladelink = Some([]string(nil)) }
	runGearCases(t, []gearCase{
		{name: "definition request", spec: parts(offset), want: 10},
		{name: "a non-pawn thing", spec: parts(offset), ctx: &StatContext{}, want: 10},
		{name: "worn apparel adds", spec: parts(offset), ctx: gearPawn(worn(gear("Apparel_Parka", 4), gear("Apparel_Parka", 1))), want: 15},
		{name: "subtract", spec: parts(subtract), ctx: gearPawn(worn(gear("Apparel_Parka", 4), gear("Apparel_Parka", 1))), want: 5},
		{name: "a bladelink trait's equipped offset joins the stat", spec: parts(offset), ctx: gearPawn(worn(traited)), want: 16},
		{name: "includeWeapon adds the primary", spec: parts(withWeapon), ctx: gearPawn(func(g *GearFacts) { worn(gear("Apparel_Parka", 4))(g); g.Primary = Some(&rifle) }), want: 19},
		{name: "includeWeapon with no primary", spec: parts(withWeapon), ctx: gearPawn(func(g *GearFacts) { worn(gear("Apparel_Parka", 4))(g); g.Primary = Some((*GearItem)(nil)) }), want: 14},
		{name: "the weapon is ignored without includeWeapon", spec: parts(offset), ctx: gearPawn(func(g *GearFacts) { worn(gear("Apparel_Parka", 4))(g); g.Primary = Some(&rifle) }), want: 14},
		{name: "the stat's parts run over a traited item", spec: recursing, ctx: gearPawn(func(g *GearFacts) { worn(traitedWithFacts)(g); g.Bladelink = Some([]string(nil)) }), want: 46},
		{name: "no equipped offset skips the parts", spec: recursing, ctx: gearPawn(noFacts), want: 14},
		{name: "apparel not observed", spec: parts(offset), ctx: gearPawn(func(*GearFacts) {}), err: "worn apparel"},
		{name: "primary not observed", spec: parts(withWeapon), ctx: gearPawn(worn(gear("Apparel_Parka", 4))), err: "primary weapon"},
		{name: "an item's stat not observed", spec: parts(offset), ctx: gearPawn(worn(GearItem{Def: "Apparel_Parka", Bladelink: Some([]string(nil))})), err: testStat},
		{name: "an item's bladelink traits not observed", spec: parts(offset), ctx: gearPawn(worn(untraitable)), err: "bladelink"},
		{name: "an unknown trait", spec: parts(offset), ctx: gearPawn(worn(badTrait)), err: "NoSuchTrait"},
		{name: "an unknown item def", spec: parts(offset), ctx: gearPawn(worn(gear("NoSuchDef", 1))), err: "NoSuchDef"},
		{name: "an unknown apparel stat", spec: parts(&d.StatPart_GearStatOffset{ApparelStat: "NoSuchStat"}), ctx: gearPawn(worn()), err: "NoSuchStat"},
	})
}

func TestGearPartWeaponTraitsMarketValueOffset(t *testing.T) {
	part := &d.StatPart_WeaponTraitsMarketValueOffset{}
	traits := func(names ...string) *StatContext {
		return gearCtx(func(g *GearFacts) { g.Bladelink = Some(names) })
	}
	runGearCases(t, []gearCase{
		{name: "definition request", spec: parts(part), want: 10},
		{name: "no traits", spec: parts(part), ctx: traits(), want: 10},
		{name: "a trait's offset", spec: parts(part), ctx: traits("TestTrait"), want: 40},
		{name: "offsets add per trait", spec: parts(part), ctx: traits("TestTrait", "TestTrait"), want: 70},
		{name: "an unknown trait", spec: parts(part), ctx: traits("NoSuchTrait"), err: "NoSuchTrait"},
		{name: "traits not observed", spec: parts(part), ctx: &StatContext{}, err: "bladelink"},
	})
}

func TestGearPartWornByCorpse(t *testing.T) {
	part := &d.StatPart_WornByCorpse{}
	notApparel := gearSpec{parts: []proto.Message{part}, edit: func(_ *recordedrows.Slice, _ *d.StatDef, parka *d.ThingDef) {
		parka.ThingClass = "Verse.Corpse"
	}}
	e, _ := parts(part).build(t)
	game, err := e.catalog.GameConstants()
	if err != nil {
		t.Fatal(err)
	}
	factor := float32(10) * game.GetStatPart_WornByCorpse().GetFactor()
	worn := func(by bool) *StatContext { return gearCtx(func(g *GearFacts) { g.WornByCorpse = Some(by) }) }
	runGearCases(t, []gearCase{
		{name: "definition request", spec: parts(part), want: 10},
		{name: "apparel worn by a corpse", spec: parts(part), ctx: worn(true), want: factor},
		{name: "apparel not worn by a corpse", spec: parts(part), ctx: worn(false), want: 10},
		{name: "a thing that is not apparel", spec: notApparel, ctx: worn(true), want: 10},
		{name: "a non-apparel needs no wearer fact", spec: notApparel, ctx: &StatContext{}, want: 10},
		{name: "wearer not observed", spec: parts(part), ctx: &StatContext{}, err: "worn by a corpse"},
	})
	if factor == 10 {
		t.Fatal("the recorded WornByCorpse factor is 1: the test proves nothing")
	}
}

// marketSpec is a catalog whose MarketValue stat has no parts, so the
// reloadable ammo's value is its statBases value.
func marketSpec(rows ...proto.Message) gearSpec {
	return gearSpec{parts: rows, edit: func(s *recordedrows.Slice, _ *d.StatDef, _ *d.ThingDef) {
		for _, stat := range s.Wire.Defs.StatDefs {
			if stat.DefName == statMarketValue {
				stat.Parts = nil
			}
		}
	}}
}

func TestGearPartReloadMarketValue(t *testing.T) {
	part := &d.StatPart_ReloadMarketValue{}
	steel, err := func() (float32, error) {
		e, _ := marketSpec(part).build(t)
		return e.Value(statMarketValue, ThingSubject("Steel", ""))
	}()
	if err != nil || steel <= 0 {
		t.Fatalf("Steel MarketValue = %v, %v", steel, err)
	}
	reloadable := func(r *ReloadableState) *StatContext {
		return gearCtx(func(g *GearFacts) { g.Reloadable = Some(r) })
	}
	runGearCases(t, []gearCase{
		{name: "definition request", spec: marketSpec(part), want: 10},
		{name: "no reloadable comp", spec: marketSpec(part), ctx: reloadable(nil), want: 10},
		{name: "a full reloadable", spec: marketSpec(part), ctx: reloadable(&ReloadableState{RemainingCharges: 3, MaxCharges: 3, AmmoDef: "Steel", MaxAmmoNeeded: 5}), want: 10},
		{name: "missing ammo costs its value", spec: marketSpec(part), ctx: reloadable(&ReloadableState{RemainingCharges: 1, MaxCharges: 3, AmmoDef: "Steel", MaxAmmoNeeded: 2}), want: float32(10 + float32(float32(0-steel)*2))},
		{name: "a charged apparel scales by its charges", spec: marketSpec(part), ctx: reloadable(&ReloadableState{RemainingCharges: 1, MaxCharges: 4, ChargedDestroyOnEmpty: true}), want: 2.5},
		{name: "a spent charged apparel is worth nothing", spec: marketSpec(part), ctx: reloadable(&ReloadableState{RemainingCharges: 0, MaxCharges: 4, ChargedDestroyOnEmpty: true}), want: 0},
		{name: "a partly spent reloadable of neither kind", spec: marketSpec(part), ctx: reloadable(&ReloadableState{RemainingCharges: 1, MaxCharges: 3}), want: 10},
		{name: "reloadable not observed", spec: marketSpec(part), ctx: &StatContext{}, err: "reloadable comp"},
		{name: "an unknown ammo", spec: marketSpec(part), ctx: reloadable(&ReloadableState{RemainingCharges: 1, MaxCharges: 3, AmmoDef: "NoSuchAmmo", MaxAmmoNeeded: 1}), err: "NoSuchAmmo"},
	})
	// The ammo's value is subtracted as a float32 product, and the result floors at 0.
	e, _ := marketSpec(part).build(t)
	subject := ThingSubject("Apparel_Parka", "")
	subject.Context = reloadable(&ReloadableState{RemainingCharges: 1, MaxCharges: 3, AmmoDef: "Steel", MaxAmmoNeeded: 2})
	req, err := e.request(testStat, subject)
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct{ val, want float32 }{
		{100, float32(100 + float32(float32(0-steel)*2))},
		{0.5, 0},
	} {
		got, err := partReloadMarketValue{}.Transform(req, part, c.val)
		if err != nil || got != c.want {
			t.Errorf("Transform(%v) = %v, %v; want %v", c.val, got, err, c.want)
		}
	}
}
