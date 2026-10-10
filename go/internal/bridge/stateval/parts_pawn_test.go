package stateval

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// pawnCase is one pawn-group part over one request. The subject is the pawn
// def (Apparel_Parka, given a race), the corpse def Corpse_Test (whose source
// race is that pawn def), or Steel, a def that is neither. ctx nil is a
// definition request.
type pawnCase struct {
	name    string
	row     proto.Message
	subject string
	ctx     *StatContext
	animal  bool
	mech    bool
	biotech bool
	anomaly bool
	want    float32
	err     string
}

const (
	pawnDef   = "Apparel_Parka"
	corpseDef = "Corpse_Test"
	itemDef   = "Steel"
)

// pawnRows are the def rows the cases read: stage Adult (body size x1.5,
// food x2), two genes, hediffs, precepts and the Mass stat of Steel (0.5).
func addPawnRows(w *d.DefSets) {
	w.LifeStageDefs = append(w.LifeStageDefs, &d.LifeStageDef{DefName: "TestAdult", BodySizeFactor: 1.5, FoodMaxFactor: 2})
	w.GeneDefs = append(w.GeneDefs,
		&d.GeneDef{DefName: "TestFastMet", BiostatMet: -2},
		&d.GeneDef{DefName: "TestSlowMet", BiostatMet: 3})
	w.HediffDefs = append(w.HediffDefs,
		&d.HediffDef{DefName: "ExecutionCut", HediffClass: "Verse.Hediff_Injury"},
		&d.HediffDef{DefName: "TestCut", HediffClass: "Verse.Hediff_Injury"},
		&d.HediffDef{DefName: "TestVirus", HediffClass: "Verse.Hediff_Injury"},
		&d.HediffDef{DefName: "TestPlain", HediffClass: "Verse.Hediff"},
		&d.HediffDef{DefName: "TestBionic", HediffClass: "Verse.Hediff_AddedPart", SpawnThingOnRemoved: itemDef},
		&d.HediffDef{DefName: "TestBionicNoItem", HediffClass: "Verse.Hediff_AddedPart"},
		&d.HediffDef{DefName: "TestNotAdded", HediffClass: "Verse.Hediff", SpawnThingOnRemoved: itemDef},
		&d.HediffDef{DefName: "ShamblerCorpse", HediffClass: "Verse.Hediff"})
	w.PreceptDefs = append(w.PreceptDefs,
		&d.PreceptDef{DefName: "TestPrecept1", BlindPsychicSensitivityOffset: 0.25, BiosculpterPodCycleSpeedFactor: 0.5, GrowthVatSpeedFactor: 2},
		&d.PreceptDef{DefName: "TestPrecept2", BlindPsychicSensitivityOffset: 0.5, BiosculpterPodCycleSpeedFactor: 0.5, GrowthVatSpeedFactor: 1.5},
		&d.PreceptDef{DefName: "TestPreceptNone"},
		&d.PreceptDef{DefName: "TestRole", ConvertPowerFactor: 1.5})
}

func newPawnRig(t *testing.T, c pawnCase) *rig {
	t.Helper()
	slice := recordedrows.Take(t, recordedrows.Named(pawnDef, itemDef), "stat_defs", "stat_category_defs", "flesh_type_defs")
	stat := &d.StatDef{
		DefName: testStat, Category: "BasicsNonPawn", WorkerClass: "RimWorld.StatWorker",
		ShowOnUntradeables: true, ShowOnUnhaulables: true, ShowIfUndefined: true,
		ApplyFactorsIfNegative: true, MinValue: -1e6, MaxValue: 1e6, RoundToFiveOver: 3.4028235e38,
		Parts: []*d.Opt_StatPartAny{wrapPart(t, c.row)},
	}
	slice.Wire.Defs.StatDefs = append(slice.Wire.Defs.StatDefs, stat)
	addPawnRows(slice.Wire.Defs)
	for _, s := range slice.Wire.Defs.StatDefs {
		if s.GetDefName() == statMass { // a plain Mass: Steel's statBases
			s.Parts = nil
			s.WorkerClass = "RimWorld.StatWorker"
		}
	}
	slice.CopyThing(itemDef, corpseDef)
	intelligence := d.Intelligence_INTELLIGENCE_HUMANLIKE
	if c.animal {
		intelligence = d.Intelligence_INTELLIGENCE_ANIMAL
	}
	race := &d.RaceProperties{Intelligence: intelligence, BaseBodySize: 2, LifeExpectancy: 80}
	if c.mech {
		race.FleshType = "Mechanoid"
	}
	pawn := slice.Thing(pawnDef)
	pawn.Race = race
	pawn.Category = d.ThingCategory_THING_CATEGORY_PAWN
	steel := slice.Thing(itemDef)
	steel.StatBases = append([]*d.Opt_StatModifier{mod(statMass, 0.5)}, steel.StatBases...) // the first entry wins
	corpse := slice.Thing(corpseDef)
	corpse.ThingClass = "Verse.Corpse"
	corpse.Ingestible = &d.IngestibleProperties{SourceDef: pawnDef}
	constants := proto.Clone(slice.Wire.GameConstants).(*d.GameConstants)
	constants.RevenantUtility.SpeedRangeFromBecameVisibleCurve = curve(0, 0.5, 10, 1.5)
	slice.Wire.GameConstants = constants
	catalog, err := recordedcatalog.FromSlice(slice, "unit")
	if err != nil {
		t.Fatal(err)
	}
	env := Env{ActiveMods: map[string]bool{"ludeon.rimworld": true}, ScenarioFactors: map[string]float32{}}
	if c.biotech {
		env.ActiveMods["ludeon.rimworld.biotech"] = true
	}
	if c.anomaly {
		env.ActiveMods["ludeon.rimworld.anomaly"] = true
	}
	return &rig{t: t, stat: stat, eval: New(catalog, env)}
}

func runPawnCases(t *testing.T, cases []pawnCase) {
	t.Helper()
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := newPawnRig(t, c)
			def := c.subject
			if def == "" {
				def = pawnDef
			}
			subject := ThingSubject(def, "")
			subject.Context = c.ctx
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

func bodyCtx(edit func(*BodyFacts)) *StatContext {
	p := &PawnState{}
	edit(&p.Body)
	return &StatContext{Pawn: p}
}

func corpseCtx(edit func(*BodyFacts)) *StatContext {
	p := &PawnState{}
	edit(&p.Body)
	return &StatContext{Corpse: p}
}

func stageAdult(b *BodyFacts) { b.CurLifeStage = Some("TestAdult") }

func hediffs(h ...HediffState) Known[[]HediffState] { return Some(h) }

func TestPawnPartBodySize(t *testing.T) {
	row := &d.StatPart_BodySize{}
	runPawnCases(t, []pawnCase{
		{name: "pawn: life stage factor times race size", row: row, ctx: bodyCtx(stageAdult), want: 30},
		{name: "corpse: inner pawn", row: row, subject: corpseDef, ctx: corpseCtx(stageAdult), want: 30},
		{name: "pawn def: race size alone", row: row, want: 20},
		{name: "corpse def: source race size", row: row, subject: corpseDef, want: 20},
		{name: "item def", row: row, subject: itemDef, want: 10},
		{name: "item thing", row: row, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "life stage not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "life stage"},
		{name: "unknown life stage def", row: row, ctx: bodyCtx(func(b *BodyFacts) { b.CurLifeStage = Some("Nope") }), err: "no life stage def Nope"},
	})
}

func TestPawnPartIsFlesh(t *testing.T) {
	row := &d.StatPart_IsFlesh{}
	runPawnCases(t, []pawnCase{
		{name: "flesh pawn", row: row, ctx: bodyCtx(func(*BodyFacts) {}), want: 10},
		{name: "mechanoid pawn", row: row, mech: true, ctx: bodyCtx(func(*BodyFacts) {}), want: 0},
		{name: "mechanoid corpse", row: row, mech: true, subject: corpseDef, ctx: corpseCtx(func(*BodyFacts) {}), want: 0},
		{name: "flesh def", row: row, want: 10},
		{name: "mechanoid def", row: row, mech: true, want: 0},
		{name: "mechanoid corpse def", row: row, mech: true, subject: corpseDef, want: 0},
		{name: "item def", row: row, subject: itemDef, want: 10},
	})
}

func TestPawnPartLifeStageMaxFood(t *testing.T) {
	row := &d.StatPart_LifeStageMaxFood{}
	runPawnCases(t, []pawnCase{
		{name: "pawn", row: row, ctx: bodyCtx(stageAdult), want: 20},
		{name: "definition request", row: row, want: 10},
		{name: "a corpse is not a pawn", row: row, subject: corpseDef, ctx: corpseCtx(stageAdult), want: 10},
		{name: "item thing", row: row, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "life stage not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "life stage"},
	})
}

func TestPawnPartMetabolismTotal(t *testing.T) {
	row := &d.StatPart_MetabolismTotal{Curve: curve(-4, 4, 0, 2, 4, 0)}
	genes := func(gs ...GeneState) *StatContext {
		return bodyCtx(func(b *BodyFacts) { b.Genes = Some(GenesState{Present: true, Genes: gs}) })
	}
	runPawnCases(t, []pawnCase{
		{name: "sum of genes", row: row, biotech: true, ctx: genes(GeneState{Def: "TestFastMet"}, GeneState{Def: "TestSlowMet"}), want: 15},                                    // x = 1
		{name: "an overridden gene is skipped", row: row, biotech: true, ctx: genes(GeneState{Def: "TestFastMet"}, GeneState{Def: "TestSlowMet", Overridden: true}), want: 30}, // x = -2
		{name: "no genes", row: row, biotech: true, ctx: genes(), want: 20},
		{name: "no gene tracker", row: row, biotech: true, ctx: bodyCtx(func(b *BodyFacts) { b.Genes = Some(GenesState{}) }), want: 10},
		{name: "biotech inactive", row: row, ctx: genes(GeneState{Def: "TestFastMet"}), want: 10},
		{name: "definition request", row: row, biotech: true, want: 10},
		{name: "non-pawn thing", row: row, biotech: true, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "genes not observed", row: row, biotech: true, ctx: bodyCtx(func(*BodyFacts) {}), err: "genes"},
		{name: "unknown gene", row: row, biotech: true, ctx: genes(GeneState{Def: "Nope"}), err: "no gene def Nope"},
	})
}

func TestPawnPartNaturalNotMissingBodyPartsCoverage(t *testing.T) {
	row := &d.StatPart_NaturalNotMissingBodyPartsCoverage{}
	cover := func(v float32) func(*BodyFacts) { return func(b *BodyFacts) { b.NaturalCoverage = Some(v) } }
	runPawnCases(t, []pawnCase{
		{name: "pawn", row: row, ctx: bodyCtx(cover(0.5)), want: 5},
		{name: "corpse", row: row, subject: corpseDef, ctx: corpseCtx(cover(0.25)), want: 2.5},
		{name: "pawn def is whole", row: row, want: 10},
		{name: "corpse def is whole", row: row, subject: corpseDef, want: 10},
		{name: "item def", row: row, subject: itemDef, want: 10},
		{name: "coverage not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "coverage"},
	})
}

func TestPawnPartNotCarefullySlaughtered(t *testing.T) {
	row := &d.StatPart_NotCarefullySlaughtered{Factor: 0.5}
	with := func(h ...HediffState) *StatContext {
		return bodyCtx(func(b *BodyFacts) { b.Hediffs = hediffs(h...) })
	}
	runPawnCases(t, []pawnCase{
		{name: "a wound", row: row, ctx: with(HediffState{Def: "TestCut"}), want: 5},
		{name: "an execution cut is careful", row: row, ctx: with(HediffState{Def: "ExecutionCut"}), want: 10},
		{name: "a permanent injury", row: row, ctx: with(HediffState{Def: "TestCut", Permanent: true}), want: 10},
		{name: "not an injury", row: row, ctx: with(HediffState{Def: "TestPlain"}), want: 10},
		{name: "a later wound counts", row: row, ctx: with(HediffState{Def: "TestPlain"}, HediffState{Def: "ExecutionCut"}, HediffState{Def: "TestVirus"}), want: 5},
		{name: "no hediffs", row: row, ctx: with(), want: 10},
		{name: "definition request", row: row, want: 10},
		{name: "a corpse is not a pawn", row: row, subject: corpseDef, ctx: corpseCtx(func(b *BodyFacts) { b.Hediffs = hediffs(HediffState{Def: "TestCut"}) }), want: 10},
		{name: "hediffs not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "hediffs"},
		{name: "unknown hediff", row: row, ctx: with(HediffState{Def: "Nope"}), err: "no hediff def Nope"},
	})
}

func TestPawnPartAddedBodyPartsMass(t *testing.T) {
	row := &d.StatPart_AddedBodyPartsMass{}
	with := func(h ...string) func(*BodyFacts) {
		return func(b *BodyFacts) {
			var hs []HediffState
			for _, name := range h {
				hs = append(hs, HediffState{Def: name})
			}
			b.Hediffs = Some(hs)
		}
	}
	var half, factor float32 = 0.5, 0.9
	one := float32(10 + float32(half*factor))
	two := float32(10 + float32(float32(half*factor)+float32(half*factor)))
	runPawnCases(t, []pawnCase{
		{name: "one added part", row: row, ctx: bodyCtx(with("TestBionic")), want: one},
		{name: "two added parts", row: row, ctx: bodyCtx(with("TestBionic", "TestBionic")), want: two},
		{name: "corpse", row: row, subject: corpseDef, ctx: corpseCtx(with("TestBionic")), want: one},
		{name: "no spawn thing", row: row, ctx: bodyCtx(with("TestBionicNoItem")), want: 10},
		{name: "not an added part", row: row, ctx: bodyCtx(with("TestNotAdded")), want: 10},
		{name: "pawn def", row: row, want: 10},
		{name: "corpse def", row: row, subject: corpseDef, want: 10},
		{name: "item", row: row, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "hediffs not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "hediffs"},
	})
}

func TestPawnPartTrainable(t *testing.T) {
	row := &d.StatPart_Trainable{TrainableDef: "Dig", Factor: 1.5}
	dig := func(learned, subhuman bool) *StatContext {
		return bodyCtx(func(b *BodyFacts) { b.DigLearned = Some(learned); b.Subhuman = Some(subhuman) })
	}
	runPawnCases(t, []pawnCase{
		{name: "an animal that learned Dig", row: row, animal: true, ctx: dig(true, false), want: 15},
		{name: "has not learned", row: row, animal: true, ctx: dig(false, false), want: 10},
		{name: "a subhuman", row: row, animal: true, ctx: dig(true, true), want: 10},
		{name: "a humanlike", row: row, ctx: bodyCtx(func(*BodyFacts) {}), want: 10},
		{name: "a mechanoid", row: row, animal: true, mech: true, ctx: bodyCtx(func(*BodyFacts) {}), want: 10},
		{name: "definition request", row: row, animal: true, want: 10},
		{name: "item thing", row: row, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "subhuman not observed", row: row, animal: true, ctx: bodyCtx(func(*BodyFacts) {}), err: "subhuman"},
		{name: "dig not observed", row: row, animal: true, ctx: bodyCtx(func(b *BodyFacts) { b.Subhuman = Some(false) }), err: "learned Dig"},
	})
}

func TestPawnPartTrainableForceShow(t *testing.T) {
	row := &d.StatPart_Trainable{Factor: 1.5}
	for _, c := range []struct {
		name   string
		ctx    *StatContext
		animal bool
		want   bool
	}{
		{"learned", bodyCtx(func(b *BodyFacts) { b.DigLearned = Some(true); b.Subhuman = Some(false) }), true, true},
		{"not learned", bodyCtx(func(b *BodyFacts) { b.DigLearned = Some(false); b.Subhuman = Some(false) }), true, false},
		{"definition request", nil, true, false},
		{"humanlike", bodyCtx(func(*BodyFacts) {}), false, false},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := newPawnRig(t, pawnCase{row: row, animal: c.animal})
			subject := ThingSubject(pawnDef, "")
			subject.Context = c.ctx
			req, err := r.eval.request(testStat, subject)
			if err != nil {
				t.Fatal(err)
			}
			got, err := partTrainable{}.ForceShow(req, row)
			if err != nil || got != c.want {
				t.Errorf("ForceShow = %v, %v, want %v", got, err, c.want)
			}
		})
	}
	r := newPawnRig(t, pawnCase{row: row, animal: true})
	subject := ThingSubject(pawnDef, "")
	subject.Context = bodyCtx(func(*BodyFacts) {})
	req, err := r.eval.request(testStat, subject)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := (partTrainable{}).ForceShow(req, row); err == nil || !strings.Contains(err.Error(), "subhuman") {
		t.Errorf("ForceShow with a missing fact = %v", err)
	}
}

func TestPawnPartTerror(t *testing.T) {
	row := &d.StatPart_Terror{}
	at := func(v float32) *StatContext { return bodyCtx(func(b *BodyFacts) { b.Terror = Some(v) }) }
	runPawnCases(t, []pawnCase{
		{name: "no terror", row: row, ctx: at(0), want: 10},
		{name: "on a curve segment", row: row, ctx: at(10), want: 4},
		{name: "at a point", row: row, ctx: at(50), want: -15},
		{name: "past the last point", row: row, ctx: at(500), want: -35},
		{name: "definition request", row: row, want: 10},
		{name: "item thing", row: row, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "terror not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "Terror"},
	})
}

func TestPawnPartShamblerCorpse(t *testing.T) {
	row := &d.StatPart_ShamblerCorpse{Multiplier: 0.25}
	with := func(h ...HediffState) *StatContext {
		return corpseCtx(func(b *BodyFacts) { b.Hediffs = hediffs(h...) })
	}
	runPawnCases(t, []pawnCase{
		{name: "a shambler corpse", row: row, anomaly: true, subject: corpseDef, ctx: with(HediffState{Def: "TestCut"}, HediffState{Def: "ShamblerCorpse"}), want: 2.5},
		{name: "an ordinary corpse", row: row, anomaly: true, subject: corpseDef, ctx: with(HediffState{Def: "TestCut"}), want: 10},
		{name: "anomaly inactive", row: row, subject: corpseDef, ctx: with(HediffState{Def: "ShamblerCorpse"}), want: 10},
		{name: "a pawn is not a corpse", row: row, anomaly: true, ctx: bodyCtx(func(b *BodyFacts) { b.Hediffs = hediffs(HediffState{Def: "ShamblerCorpse"}) }), want: 10},
		{name: "definition request", row: row, anomaly: true, subject: corpseDef, want: 10},
		{name: "hediffs not observed", row: row, anomaly: true, subject: corpseDef, ctx: corpseCtx(func(*BodyFacts) {}), err: "hediffs"},
	})
}

func TestPawnPartShamblerCrawling(t *testing.T) {
	row := &d.StatPart_ShamblerCrawling{Factor: 0.5}
	state := func(spawned, shambler, crawling bool) *StatContext {
		c := bodyCtx(func(b *BodyFacts) { b.Crawling = Some(crawling) })
		c.Spawned = Some(spawned)
		c.Pawn.Shambler = Some(shambler)
		return c
	}
	runPawnCases(t, []pawnCase{
		{name: "crawling shambler", row: row, ctx: state(true, true, true), want: 5},
		{name: "walking shambler", row: row, ctx: state(true, true, false), want: 10},
		{name: "crawling pawn that is no shambler", row: row, ctx: state(true, false, true), want: 10},
		{name: "unspawned", row: row, ctx: state(false, true, true), want: 10},
		{name: "definition request", row: row, want: 10},
		{name: "spawned non-pawn", row: row, subject: itemDef, ctx: &StatContext{Spawned: Some(true)}, want: 10},
		{name: "spawned not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "spawned"},
		{name: "shambler not observed", row: row, ctx: &StatContext{Spawned: Some(true), Pawn: &PawnState{}}, err: "shambler"},
		{name: "crawling not observed", row: row, ctx: &StatContext{Spawned: Some(true), Pawn: &PawnState{Shambler: Some(true)}}, err: "crawling"},
	})
}

func TestPawnPartRevenantSpeed(t *testing.T) {
	row := &d.StatPart_RevenantSpeed{}
	// The speed curve is 0.5 at 0 s and 1.5 at 10 s; 60 ticks a second.
	rev := func(r RevenantState, now int32) *StatContext {
		return bodyCtx(func(b *BodyFacts) { b.Revenant = Some(r); b.TicksGame = Some(now) })
	}
	visible := RevenantState{KindDef: "Revenant", LastBecameVisibleTick: 1000}
	runPawnCases(t, []pawnCase{
		{name: "150 ticks after becoming visible", row: row, ctx: rev(visible, 1150), want: 7.5},
		{name: "forced visible later", row: row, ctx: rev(RevenantState{KindDef: "Revenant", LastBecameVisibleTick: 1000, LastForcedVisibleTick: 1300}, 1450), want: 7.5},
		{name: "forced visible earlier", row: row, ctx: rev(RevenantState{KindDef: "Revenant", LastBecameVisibleTick: 1300, LastForcedVisibleTick: 1000}, 1450), want: 7.5},
		{name: "past the curve", row: row, ctx: rev(visible, 100000), want: 15},
		{name: "never visible", row: row, ctx: rev(RevenantState{KindDef: "Revenant"}, 1300), want: 10},
		{name: "invisible", row: row, ctx: rev(RevenantState{KindDef: "Revenant", LastBecameVisibleTick: 1000, PsychologicallyInvisible: true}, 1300), want: 10},
		{name: "another kind needs nothing else", row: row, ctx: bodyCtx(func(b *BodyFacts) { b.Revenant = Some(RevenantState{KindDef: "Colonist"}) }), want: 10},
		{name: "definition request", row: row, want: 10},
		{name: "revenant state not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "revenant"},
		{name: "tick not observed", row: row, ctx: bodyCtx(func(b *BodyFacts) { b.Revenant = Some(visible) }), err: "game tick"},
	})
}

func TestPawnPartTerrainMoveSpeed(t *testing.T) {
	row := &d.StatPart_TerrainMoveSpeed{}
	runPawnCases(t, []pawnCase{
		{name: "pawn", row: row, ctx: bodyCtx(func(*BodyFacts) {}), want: 10},
		{name: "definition request", row: row, want: 10},
	})
}

func TestPawnPartBlindPsychicSensitivityOffset(t *testing.T) {
	row := &d.StatPart_BlindPsychicSensitivityOffset{}
	blind := func(all bool, ideo IdeoState) *StatContext {
		return bodyCtx(func(b *BodyFacts) { b.SightSourcesAllMissing = Some(all); b.Ideo = Some(ideo) })
	}
	runPawnCases(t, []pawnCase{
		{name: "blind with precepts", row: row, ctx: blind(true, IdeoState{Present: true, Precepts: []string{"TestPrecept1", "TestPrecept2", "TestPreceptNone"}}), want: 10.75},
		{name: "a precept offsetting to nothing", row: row, ctx: blind(true, IdeoState{Present: true, Precepts: []string{"TestPreceptNone"}}), want: 10},
		{name: "sighted", row: row, ctx: blind(false, IdeoState{Present: true, Precepts: []string{"TestPrecept1"}}), want: 10},
		{name: "no ideo", row: row, ctx: blind(true, IdeoState{}), want: 10},
		{name: "definition request", row: row, want: 10},
		{name: "sighted needs no ideo", row: row, ctx: bodyCtx(func(b *BodyFacts) { b.SightSourcesAllMissing = Some(false) }), want: 10},
		{name: "blindness not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "sight source"},
		{name: "ideo not observed", row: row, ctx: bodyCtx(func(b *BodyFacts) { b.SightSourcesAllMissing = Some(true) }), err: "ideo"},
		{name: "unknown precept", row: row, ctx: blind(true, IdeoState{Present: true, Precepts: []string{"Nope"}}), err: "no precept def Nope"},
	})
}

func TestPawnPartSightPsychicSensitivityOffset(t *testing.T) {
	row := &d.StatPart_SightPsychicSensitivityOffset{StartsAt: 0.5, MinBonus: 0, MaxBonus: 0.5, EndsAt: 0}
	eff := func(v float32) *StatContext { return bodyCtx(func(b *BodyFacts) { b.SightEfficiency = Some(v) }) }
	runPawnCases(t, []pawnCase{
		{name: "blind", row: row, ctx: eff(0), want: 10.5},
		{name: "half way", row: row, ctx: eff(0.25), want: 10.25},
		{name: "at startsAt the bonus is zero", row: row, ctx: eff(0.5), want: 10},
		{name: "above startsAt", row: row, ctx: eff(1), want: 10},
		{name: "below the 0.01 floor", row: row, ctx: eff(0.495), want: 10},
		{name: "above the floor", row: row, ctx: eff(0.375), want: 10.125},
		{name: "definition request", row: row, want: 10},
		{name: "non-pawn thing", row: row, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "efficiency not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "sight tag efficiency"},
	})
}

func TestPawnPartOverseerStatOffset(t *testing.T) {
	row := &d.StatPart_OverseerStatOffset{Stat: "WorkSpeedGlobal", Label: "x"}
	overseer := func(o OverseerState) *StatContext { return bodyCtx(func(b *BodyFacts) { b.Overseer = Some(o) }) }
	runPawnCases(t, []pawnCase{
		{name: "overseer", row: row, biotech: true, ctx: overseer(OverseerState{Present: true, Stats: map[string]float32{"WorkSpeedGlobal": 0.5}}), want: 10.5},
		{name: "no overseer", row: row, biotech: true, ctx: overseer(OverseerState{}), want: 10},
		{name: "biotech inactive", row: row, ctx: overseer(OverseerState{Present: true, Stats: map[string]float32{"WorkSpeedGlobal": 0.5}}), want: 10},
		{name: "definition request", row: row, biotech: true, want: 10},
		{name: "non-pawn thing", row: row, biotech: true, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "overseer not observed", row: row, biotech: true, ctx: bodyCtx(func(*BodyFacts) {}), err: "overseer"},
		{name: "overseer stat not observed", row: row, biotech: true, ctx: overseer(OverseerState{Present: true}), err: "WorkSpeedGlobal"},
	})
}

func TestPawnPartRoleConversionPower(t *testing.T) {
	row := &d.StatPart_RoleConversionPower{}
	ideo := func(i IdeoState) *StatContext { return bodyCtx(func(b *BodyFacts) { b.Ideo = Some(i) }) }
	runPawnCases(t, []pawnCase{
		{name: "role", row: row, ctx: ideo(IdeoState{Present: true, Role: "TestRole"}), want: 15},
		{name: "no role", row: row, ctx: ideo(IdeoState{Present: true}), want: 10},
		{name: "no ideo", row: row, ctx: ideo(IdeoState{}), want: 10},
		{name: "definition request", row: row, want: 10},
		{name: "ideo not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "ideo"},
		{name: "unknown role", row: row, ctx: ideo(IdeoState{Present: true, Role: "Nope"}), err: "no precept def Nope"},
	})
}

func TestPawnPartPlayerFactionLeader(t *testing.T) {
	row := &d.StatPart_PlayerFactionLeader{Offset: 0.25}
	leader := func(v bool) *StatContext { return bodyCtx(func(b *BodyFacts) { b.PlayerFactionLeader = Some(v) }) }
	runPawnCases(t, []pawnCase{
		{name: "leader", row: row, ctx: leader(true), want: 10.25},
		{name: "not the leader", row: row, ctx: leader(false), want: 10},
		{name: "definition request", row: row, want: 10},
		{name: "non-pawn thing", row: row, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "leader not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "leads the player faction"},
	})
}

func TestPawnPartBedStat(t *testing.T) {
	row := &d.StatPart_BedStat{Stat: "ImmunityGainSpeed"}
	bed := func(kind string, stats map[string]float32) *StatContext {
		return bodyCtx(func(b *BodyFacts) { b.Bed = Some(BedState{Kind: kind, Stats: stats}) })
	}
	stats := map[string]float32{"ImmunityGainSpeed": 1.5}
	runPawnCases(t, []pawnCase{
		{name: "in a bed", row: row, ctx: bed(BedBuilt, stats), want: 15},
		{name: "in a caravan bed", row: row, ctx: bed(BedCaravan, stats), want: 15},
		{name: "out of bed needs no stat", row: row, ctx: bed(BedNone, nil), want: 10},
		{name: "definition request", row: row, want: 10},
		{name: "non-pawn thing", row: row, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "bed not observed", row: row, ctx: bodyCtx(func(*BodyFacts) {}), err: "bed"},
		{name: "bed stat not observed", row: row, ctx: bed(BedBuilt, nil), err: "ImmunityGainSpeed"},
		{name: "unknown bed kind", row: row, ctx: bed("Chair", stats), err: "Chair"},
	})
}

func TestPawnPartPreceptSpeedFactors(t *testing.T) {
	sculpt, vat := &d.StatPart_BiosculptingSpeedFactor{}, &d.StatPart_GrowthVatSpeedFactor{}
	ideo := func(i IdeoState) *StatContext { return bodyCtx(func(b *BodyFacts) { b.Ideo = Some(i) }) }
	two := IdeoState{Present: true, Precepts: []string{"TestPrecept1", "TestPrecept2", "TestPrecept1"}}
	runPawnCases(t, []pawnCase{
		{name: "biosculpting: product of precepts", row: sculpt, ctx: ideo(two), want: 1.25},
		{name: "biosculpting: ideo without precepts", row: sculpt, ctx: ideo(IdeoState{Present: true}), want: 10},
		{name: "biosculpting: a precept with factor 0", row: sculpt, ctx: ideo(IdeoState{Present: true, Precepts: []string{"TestPreceptNone"}}), want: 0},
		{name: "biosculpting: no ideo", row: sculpt, ctx: ideo(IdeoState{}), want: 10},
		{name: "biosculpting: definition request", row: sculpt, want: 10},
		{name: "biosculpting: ideo not observed", row: sculpt, ctx: bodyCtx(func(*BodyFacts) {}), err: "ideo"},
		{name: "growth vat: product of precepts", row: vat, ctx: ideo(two), want: 60},
		{name: "growth vat: no ideo", row: vat, ctx: ideo(IdeoState{}), want: 10},
		{name: "growth vat: definition request", row: vat, want: 10},
		{name: "growth vat: non-pawn thing", row: vat, subject: itemDef, ctx: &StatContext{}, want: 10},
		{name: "growth vat: ideo not observed", row: vat, ctx: bodyCtx(func(*BodyFacts) {}), err: "ideo"},
	})
}
