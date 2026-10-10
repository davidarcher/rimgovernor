package stateval

import (
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The base worker's thing-side terms (#2639), one test per term over a small
// catalog: the stat is TestStat (base 10), "OtherStat" is a second stat the
// terms read through the thing (statFactors, effect multipliers), and the
// thing is Apparel_Parka made a humanlike pawn when a test gives it a PawnState.

const otherStat = "OtherStat"

type baseOpts struct {
	stat    func(*d.StatDef)
	other   func(*d.StatDef)
	parka   func(*d.ThingDef)
	steel   func(*d.ThingDef)
	defs    func(*d.DefSets)
	chains  []string
	biotech bool
}

func plainStat(name string) *d.StatDef {
	return &d.StatDef{
		DefName: name, Category: "BasicsNonPawn", WorkerClass: "RimWorld.StatWorker",
		ShowOnUntradeables: true, ShowOnUnhaulables: true, ShowIfUndefined: true,
		ApplyFactorsIfNegative: true, MinValue: -1e6, MaxValue: 1e6, RoundToFiveOver: 3.4028235e38, NoSkillFactor: 1,
	}
}

func newBaseRig(t *testing.T, opts baseOpts) *rig {
	t.Helper()
	slice := recordedrows.Take(t, recordedrows.Named("Apparel_Parka", "Steel"), "stat_defs", "stat_category_defs", "flesh_type_defs")
	stat, other := plainStat(testStat), plainStat(otherStat)
	stat.DefaultBaseValue = 10
	var parka, steel *d.ThingDef
	for _, row := range slice.Wire.ThingDefs {
		switch row.DefName {
		case "Apparel_Parka":
			parka = row
		case "Steel":
			steel = row
		}
	}
	parka.Race = &d.RaceProperties{Intelligence: d.Intelligence_INTELLIGENCE_HUMANLIKE, LifeExpectancy: 80}
	parka.StatBases = nil
	steel.StatBases = nil
	parka.EquippedStatOffsets = nil
	steel.EquippedStatOffsets = nil
	if opts.stat != nil {
		opts.stat(stat)
	}
	if opts.other != nil {
		opts.other(other)
	}
	if opts.parka != nil {
		opts.parka(parka)
	}
	if opts.steel != nil {
		opts.steel(steel)
	}
	if opts.defs != nil {
		opts.defs(slice.Wire.Defs)
	}
	have := map[string]bool{}
	for _, c := range slice.Wire.ClassChains {
		have[c.Name] = true
	}
	for _, class := range opts.chains {
		if !have[class] {
			slice.Wire.ClassChains = append(slice.Wire.ClassChains, &o.ClassChain{Name: class})
		}
	}
	slice.Wire.Defs.StatDefs = slices.DeleteFunc(slices.Clone(slice.Wire.Defs.StatDefs), func(s *d.StatDef) bool { return s.DefName == stat.DefName })
	slice.Wire.Defs.StatDefs = append(slice.Wire.Defs.StatDefs, stat, other)
	catalog, err := recordedcatalog.FromSlice(slice, "unit")
	if err != nil {
		t.Fatal(err)
	}
	mods := map[string]bool{"ludeon.rimworld": true}
	if opts.biotech {
		mods[modBiotech] = true
	}
	return &rig{t: t, stat: stat, eval: New(catalog, Env{ActiveMods: mods, ScenarioFactors: map[string]float32{}})}
}

// statedPawn is a pawn with every base fact stated as "nothing there".
func statedPawn(edit func(*PawnState)) *StatContext {
	p := &PawnState{Body: BodyFacts{
		Hediffs:      Some([]HediffState{}),
		Ideo:         Some(IdeoState{}),
		Genes:        Some(GenesState{}),
		CurLifeStage: Some("TestLife"),
	}, Base: PawnBaseFacts{
		Skills:      Some(SkillsState{Present: true, Levels: map[string]int32{}}),
		Capacities:  Some(map[string]float32{}),
		Story:       Some(StoryState{Present: true}),
		Apparel:     Some(WornGear{Present: true}),
		Primary:     Some[*GearPiece](nil),
		Inspiration: Some(""),
	}}
	if edit != nil {
		edit(p)
	}
	return &StatContext{Pawn: p, Base: BaseFacts{Quality: Some(QualityState{})}}
}

func lifeStages(offsets, factors []*d.Opt_StatModifier) func(*d.DefSets) {
	return func(s *d.DefSets) {
		s.LifeStageDefs = append(s.LifeStageDefs, &d.LifeStageDef{DefName: "TestLife", StatOffsets: offsets, StatFactors: factors})
	}
}

func withDefs(fns ...func(*d.DefSets)) func(*d.DefSets) {
	fns = slices.DeleteFunc(slices.Clone(fns), func(f func(*d.DefSets)) bool { return f == nil })
	return func(s *d.DefSets) {
		for _, f := range fns {
			f(s)
		}
	}
}

func onThing(ctx *StatContext) Subject {
	s := ThingSubject("Apparel_Parka", "")
	s.Context = ctx
	return s
}

func (r *rig) at(ctx *StatContext) float32 {
	r.t.Helper()
	v, err := r.eval.Value(testStat, onThing(ctx))
	if err != nil {
		r.t.Fatal(err)
	}
	return v
}

func (r *rig) fails(ctx *StatContext, want string) {
	r.t.Helper()
	_, err := r.eval.Value(testStat, onThing(ctx))
	if err == nil || !strings.Contains(err.Error(), want) {
		r.t.Fatalf("error = %v, want one containing %q", err, want)
	}
}

func checkValue(t *testing.T, got, want float32) {
	t.Helper()
	if got != want {
		t.Errorf("= %v, want %v", got, want)
	}
}

func skillDirect(skill string, values ...float32) *d.Opt_SkillNeedAny {
	return &d.Opt_SkillNeedAny{Value: &d.SkillNeedAny{Value: &d.SkillNeedAny_SkillNeed_Direct{SkillNeed_Direct: &d.SkillNeed_Direct{Skill: skill, ValuesPerLevel: values}}}}
}

func skillBaseBonus(skill string, base, bonus float32) *d.Opt_SkillNeedAny {
	return &d.Opt_SkillNeedAny{Value: &d.SkillNeedAny{Value: &d.SkillNeedAny_SkillNeed_BaseBonus{SkillNeed_BaseBonus: &d.SkillNeed_BaseBonus{Skill: skill, BaseValue: base, BonusPerLevel: bonus}}}}
}

func skillCurve(skill string, c *d.SimpleCurve) *d.Opt_SkillNeedAny {
	return &d.Opt_SkillNeedAny{Value: &d.SkillNeedAny{Value: &d.SkillNeedAny_SkillNeed_Curve{SkillNeed_Curve: &d.SkillNeed_Curve{Skill: skill, Curve: c}}}}
}

func skillsAt(levels map[string]int32) func(*PawnState) {
	return func(p *PawnState) { p.Base.Skills = Some(SkillsState{Present: true, Levels: levels}) }
}

func TestBaseSkillNeedOffsets(t *testing.T) {
	r := newBaseRig(t, baseOpts{
		stat: func(s *d.StatDef) {
			s.SkillNeedOffsets = []*d.Opt_SkillNeedAny{
				skillDirect("Mining", 0.5, 1, 1.5),
				skillBaseBonus("Mining", 0.5, 0.25),
				skillCurve("Mining", curve(0, 1, 10, 2)),
			}
			s.NoSkillOffset = 3
		},
		defs: lifeStages(nil, nil),
	})
	// Level 2: 1.5 + (0.5+0.25*2) + curve(2)=1.2 -> 10+1.5+1+1.2
	checkValue(t, r.at(statedPawn(skillsAt(map[string]int32{"Mining": 2}))), 13.7)
	// Level 10 clamps Direct to its last value and reaches the curve's end.
	checkValue(t, r.at(statedPawn(skillsAt(map[string]int32{"Mining": 10}))), 10+1.5+3+2)
	r.fails(statedPawn(skillsAt(map[string]int32{"Plants": 2})), "Mining skill level")
	// A pawn without skills takes the no-skill offset instead.
	checkValue(t, r.at(statedPawn(func(p *PawnState) { p.Base.Skills = Some(SkillsState{}) })), 13)
	r.fails(statedPawn(func(p *PawnState) { p.Base.Skills = Known[SkillsState]{} }), "pawn's skills")
}

func TestBaseSkillNeedDirectEmptyAndBase(t *testing.T) {
	r := newBaseRig(t, baseOpts{
		stat: func(s *d.StatDef) { s.SkillNeedOffsets = []*d.Opt_SkillNeedAny{skillDirect("Mining")} },
		defs: lifeStages(nil, nil),
	})
	// No values: 1.
	checkValue(t, r.at(statedPawn(skillsAt(map[string]int32{"Mining": 4}))), 11)
	// The base SkillNeed throws in the game: an error here.
	bad := newBaseRig(t, baseOpts{
		stat: func(s *d.StatDef) {
			s.SkillNeedOffsets = []*d.Opt_SkillNeedAny{{Value: &d.SkillNeedAny{Value: &d.SkillNeedAny_SkillNeed{SkillNeed: &d.SkillNeed{Skill: "Mining"}}}}}
		},
		defs: lifeStages(nil, nil),
	})
	bad.fails(statedPawn(skillsAt(map[string]int32{"Mining": 4})), "SkillNeed")
}

func TestBaseSkillNeedFactorsAndNoSkillFactor(t *testing.T) {
	r := newBaseRig(t, baseOpts{
		stat: func(s *d.StatDef) {
			s.SkillNeedFactors = []*d.Opt_SkillNeedAny{skillDirect("Mining", 1, 2, 3)}
			s.NoSkillFactor = 0.5
			s.NoSkillOffset = 2
		},
		defs: lifeStages(nil, nil),
	})
	checkValue(t, r.at(statedPawn(skillsAt(map[string]int32{"Mining": 1}))), 20)
	// No skills: offset 2 then factor 0.5.
	checkValue(t, r.at(statedPawn(func(p *PawnState) { p.Base.Skills = Some(SkillsState{}) })), 6)
}

func TestBaseCapacityOffsetsAndFactors(t *testing.T) {
	r := newBaseRig(t, baseOpts{
		stat: func(s *d.StatDef) {
			s.CapacityOffsets = []*d.Opt_PawnCapacityOffset{{Value: &d.PawnCapacityOffset{Capacity: "Moving", Scale: 2, Max: 1.5}}}
		},
		defs: lifeStages(nil, nil),
	})
	caps := func(v float32) func(*PawnState) {
		return func(p *PawnState) { p.Base.Capacities = Some(map[string]float32{"Moving": v}) }
	}
	checkValue(t, r.at(statedPawn(caps(1.25))), 10.5)
	checkValue(t, r.at(statedPawn(caps(2))), 11) // clamped to max 1.5
	r.fails(statedPawn(nil), "Moving capacity level")
	r.fails(statedPawn(func(p *PawnState) { p.Base.Capacities = Known[map[string]float32]{} }), "capacity levels")

	factor := func(cf *d.PawnCapacityFactor) *rig {
		return newBaseRig(t, baseOpts{
			stat: func(s *d.StatDef) { s.CapacityFactors = []*d.Opt_PawnCapacityFactor{{Value: cf}} },
			defs: lifeStages(nil, nil),
		})
	}
	// Lerp(10, 10*0.5, 0.5) = 7.5
	checkValue(t, factor(&d.PawnCapacityFactor{Capacity: "Moving", Weight: 0.5, Max: 9999}).at(statedPawn(caps(0.5))), 7.5)
	// Reciprocal: 1/0.5 = 2; Lerp(10, 20, 0.5) = 15.
	checkValue(t, factor(&d.PawnCapacityFactor{Capacity: "Moving", Weight: 0.5, Max: 9999, UseReciprocal: true}).at(statedPawn(caps(0.5))), 15)
	// Reciprocal of a tiny level caps at 5: Lerp(10, 50, 0.5) = 30.
	checkValue(t, factor(&d.PawnCapacityFactor{Capacity: "Moving", Weight: 0.5, Max: 9999, UseReciprocal: true}).at(statedPawn(caps(0.0005))), 30)
	// Allowed defect 0.5: level 0.25 -> InverseLerp(0, 0.5, 0.25) = 0.5.
	checkValue(t, factor(&d.PawnCapacityFactor{Capacity: "Moving", Weight: 1, Max: 9999, AllowedDefect: 0.5}).at(statedPawn(caps(0.25))), 5)
	// The max caps the level before the factor: level 4 -> 2.
	checkValue(t, factor(&d.PawnCapacityFactor{Capacity: "Moving", Weight: 1, Max: 2}).at(statedPawn(caps(4))), 20)
	// Weight beyond 1 clamps in Lerp.
	checkValue(t, factor(&d.PawnCapacityFactor{Capacity: "Moving", Weight: 3, Max: 9999}).at(statedPawn(caps(0.5))), 5)
}

func trait(name string, degree int32, offsets, factors []*d.Opt_StatModifier) *d.TraitDef {
	return &d.TraitDef{DefName: name, DegreeDatas: []*d.Opt_TraitDegreeData{{Value: &d.TraitDegreeData{Degree: degree, StatOffsets: offsets, StatFactors: factors}}}}
}

func TestBaseTraits(t *testing.T) {
	t1 := trait("T1", 0, []*d.Opt_StatModifier{mod(testStat, 1), mod(otherStat, 5), mod(testStat, 2)}, []*d.Opt_StatModifier{mod(testStat, 2), mod(testStat, 3)})
	t1.DegreeDatas = append(t1.DegreeDatas, &d.Opt_TraitDegreeData{Value: &d.TraitDegreeData{Degree: 1, StatOffsets: []*d.Opt_StatModifier{mod(testStat, 10)}}})
	t2 := trait("T2", 0, nil, []*d.Opt_StatModifier{mod(testStat, 0.5)})
	r := newBaseRig(t, baseOpts{
		defs: withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.TraitDefs = append(s.TraitDefs, t1, t2) }),
	})
	story := func(traits ...TraitState) *StatContext {
		return statedPawn(func(p *PawnState) { p.Base.Story = Some(StoryState{Present: true, Traits: traits}) })
	}
	// Every matching entry adds (1+2) and multiplies (2*3): (10+3)*6.
	checkValue(t, r.at(story(TraitState{Def: "T1"})), 78)
	// Degree 1 offsets 10; no factor entries.
	checkValue(t, r.at(story(TraitState{Def: "T1", Degree: 1})), 20)
	// An unknown degree falls back to the first defined.
	checkValue(t, r.at(story(TraitState{Def: "T1", Degree: 7})), 78)
	// A suppressed trait contributes nothing; the next one multiplies.
	checkValue(t, r.at(story(TraitState{Def: "T1", Suppressed: true}, TraitState{Def: "T2"})), 5)
	// No story: no traits.
	checkValue(t, r.at(statedPawn(func(p *PawnState) { p.Base.Story = Some(StoryState{}) })), 10)
	r.fails(story(TraitState{Def: "Nope"}), "trait def Nope")
	r.fails(statedPawn(func(p *PawnState) { p.Base.Story = Known[StoryState]{} }), "pawn's story")
}

func baseHediffDef(name string, stages ...*d.HediffStage) *d.HediffDef {
	h := &d.HediffDef{DefName: name}
	for _, s := range stages {
		h.Stages = append(h.Stages, &d.Opt_HediffStage{Value: s})
	}
	return h
}

func hediffsOf(hs ...HediffState) func(*PawnState) {
	return func(p *PawnState) { p.Body.Hediffs = Some(hs) }
}

func TestBaseHediffs(t *testing.T) {
	stages := []*d.HediffStage{
		/*0*/ {StatOffsets: []*d.Opt_StatModifier{mod(testStat, 2)}},
		/*1*/ {StatOffsets: []*d.Opt_StatModifier{mod(testStat, 4)}, MultiplyStatChangesBySeverity: true},
		/*2*/ {StatFactors: []*d.Opt_StatModifier{mod(testStat, 0.5)}},
		/*3*/ {StatOffsetsBySeverity: []*d.Opt_StatModifierBySeverity{{Value: &d.StatModifierBySeverity{Stat: testStat, ValueBySeverity: curve(0, 0, 1, 8)}}}},
		/*4*/ {StatOffsets: []*d.Opt_StatModifier{mod(testStat, 4)}, StatOffsetEffectMultiplier: otherStat},
		/*5*/ {StatFactors: []*d.Opt_StatModifier{mod(testStat, 0.5)}, StatFactorEffectMultiplier: otherStat},
		/*6*/ {StatFactorsBySeverity: []*d.Opt_StatModifierBySeverity{{Value: &d.StatModifierBySeverity{Stat: testStat, ValueBySeverity: curve(0, 1, 1, 0.5)}}}},
		/*7*/ {StatFactors: []*d.Opt_StatModifier{mod(testStat, 0.5)}, MultiplyStatChangesBySeverity: true},
		/*8*/ {},
	}
	r := newBaseRig(t, baseOpts{
		parka: func(p *d.ThingDef) { p.StatBases = []*d.Opt_StatModifier{mod(otherStat, 0.5)} },
		defs:  withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.HediffDefs = append(s.HediffDefs, baseHediffDef("H", stages...)) }),
	})
	at := func(stage int32, severity float32) float32 {
		return r.at(statedPawn(hediffsOf(HediffState{Def: "H", Severity: severity, Stage: HediffStageState{Kind: StageDef, Index: stage}})))
	}
	checkValue(t, at(0, 0.5), 12)
	checkValue(t, at(1, 0.5), 12)  // 4 * severity 0.5
	checkValue(t, at(2, 0.5), 5)   // factor
	checkValue(t, at(3, 0.25), 12) // curve(0.25) = 2
	checkValue(t, at(4, 0.5), 12)  // 4 * OtherStat(0.5)
	checkValue(t, at(5, 0.5), 7.5) // ScaleFactor(0.5, 0.5) = 0.75
	checkValue(t, at(6, 1), 5)     // curve factor at severity 1
	checkValue(t, at(7, 0.5), 7.5) // ScaleFactor(0.5, severity 0.5)
	checkValue(t, at(8, 0.5), 10)  // empty stage
	// Offsets run before factors regardless of hediff order: (10+2)*0.5.
	both := hediffsOf(
		HediffState{Def: "H", Severity: 1, Stage: HediffStageState{Kind: StageDef, Index: 2}},
		HediffState{Def: "H", Severity: 1, Stage: HediffStageState{Kind: StageDef, Index: 0}})
	checkValue(t, r.at(statedPawn(both)), 6)
	// No stage; a runtime stage with its own offsets and factors.
	checkValue(t, r.at(statedPawn(hediffsOf(HediffState{Def: "H", Stage: HediffStageState{Kind: StageNone}}))), 10)
	synthetic := HediffState{Def: "H", Stage: HediffStageState{Kind: StageSynthetic, StatOffsets: []StatMod{{Stat: testStat, Value: 3}}, StatFactors: []StatMod{{Stat: testStat, Value: 2}}}}
	checkValue(t, r.at(statedPawn(hediffsOf(synthetic))), 26)
	r.fails(statedPawn(hediffsOf(HediffState{Def: "H", Stage: HediffStageState{Kind: StageDef, Index: 99}})), "no stage 99")
	r.fails(statedPawn(hediffsOf(HediffState{Def: "Nope", Stage: HediffStageState{Kind: StageDef}})), "hediff def Nope")
	r.fails(statedPawn(hediffsOf(HediffState{Def: "H", Stage: HediffStageState{Kind: 9}})), "unknown stage kind")
	r.fails(statedPawn(func(p *PawnState) { p.Body.Hediffs = Known[[]HediffState]{} }), "pawn's hediffs")
}

func conditional(class string, offsets, factors []*d.Opt_StatModifier) *d.Opt_ConditionalStatAffecterAny {
	any := &d.ConditionalStatAffecterAny{}
	switch class {
	case "Child":
		any.Value = &d.ConditionalStatAffecterAny_ConditionalStatAffecter_Child{ConditionalStatAffecter_Child: &d.ConditionalStatAffecter_Child{StatOffsets: offsets, StatFactors: factors}}
	case "Clothed":
		any.Value = &d.ConditionalStatAffecterAny_ConditionalStatAffecter_Clothed{ConditionalStatAffecter_Clothed: &d.ConditionalStatAffecter_Clothed{StatOffsets: offsets, StatFactors: factors}}
	case "Unclothed":
		any.Value = &d.ConditionalStatAffecterAny_ConditionalStatAffecter_Unclothed{ConditionalStatAffecter_Unclothed: &d.ConditionalStatAffecter_Unclothed{StatOffsets: offsets, StatFactors: factors}}
	case "InSpace":
		any.Value = &d.ConditionalStatAffecterAny_ConditionalStatAffecter_InSpace{ConditionalStatAffecter_InSpace: &d.ConditionalStatAffecter_InSpace{StatOffsets: offsets, StatFactors: factors}}
	case "NotInSpace":
		any.Value = &d.ConditionalStatAffecterAny_ConditionalStatAffecter_NotInSpace{ConditionalStatAffecter_NotInSpace: &d.ConditionalStatAffecter_NotInSpace{StatOffsets: offsets, StatFactors: factors}}
	case "InSunlight":
		any.Value = &d.ConditionalStatAffecterAny_ConditionalStatAffecter_InSunlight{ConditionalStatAffecter_InSunlight: &d.ConditionalStatAffecter_InSunlight{StatOffsets: offsets, StatFactors: factors}}
	}
	return &d.Opt_ConditionalStatAffecterAny{Value: any}
}

func roleOffset(stat string, v float32) *d.Opt_RoleEffectAny {
	return &d.Opt_RoleEffectAny{Value: &d.RoleEffectAny{Value: &d.RoleEffectAny_RoleEffect_PawnStatOffset{RoleEffect_PawnStatOffset: &d.RoleEffect_PawnStatOffset{StatDef: stat, Modifier: v}}}}
}

func roleFactor(stat string, v float32) *d.Opt_RoleEffectAny {
	return &d.Opt_RoleEffectAny{Value: &d.RoleEffectAny{Value: &d.RoleEffectAny_RoleEffect_PawnStatFactor{RoleEffect_PawnStatFactor: &d.RoleEffect_PawnStatFactor{StatDef: stat, Modifier: v}}}}
}

func clothedParka(p *d.ThingDef) {
	p.Apparel = &d.ApparelProperties{CountsAsClothingForNudity: true}
}

func wearing(def string) func(*PawnState) {
	return func(p *PawnState) {
		p.Base.Apparel = Some(WornGear{Present: true, Worn: []GearPiece{{Subject: ThingSubject(def, "")}}})
	}
}

func TestBaseIdeoPreceptsAndRole(t *testing.T) {
	p1 := &d.PreceptDef{
		DefName:     "P1",
		StatOffsets: []*d.Opt_StatModifier{mod(testStat, 1)},
		StatFactors: []*d.Opt_StatModifier{mod(testStat, 2)},
		ConditionalStatAffecters: []*d.Opt_ConditionalStatAffecterAny{
			conditional("Clothed", []*d.Opt_StatModifier{mod(testStat, 3)}, []*d.Opt_StatModifier{mod(testStat, 3)}),
		},
	}
	role := &d.PreceptDef{DefName: "R", RoleEffects: []*d.Opt_RoleEffectAny{roleOffset(testStat, 5), roleFactor(testStat, 0.5), roleOffset(otherStat, 9), roleFactor(otherStat, 9)}}
	defs := withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.PreceptDefs = append(s.PreceptDefs, p1, role) })
	ideo := func(extra func(*PawnState)) *StatContext {
		return statedPawn(func(p *PawnState) {
			p.Body.Ideo = Some(IdeoState{Present: true, Precepts: []string{"P1"}, Role: "R"})
			if extra != nil {
				extra(p)
			}
		})
	}
	// Without Biotech the clothed affecter never applies: (10+1+5)*2*0.5.
	off := newBaseRig(t, baseOpts{defs: defs, parka: clothedParka})
	checkValue(t, off.at(ideo(wearing("Apparel_Parka"))), 16)
	// With Biotech and a clothing-for-nudity piece: (10+1+3+5)*2*3*0.5.
	on := newBaseRig(t, baseOpts{defs: defs, parka: clothedParka, biotech: true})
	checkValue(t, on.at(ideo(wearing("Apparel_Parka"))), 57)
	// Naked: Clothed does not apply.
	checkValue(t, on.at(ideo(nil)), 16)
	on.fails(statedPawn(func(p *PawnState) { p.Body.Ideo = Known[IdeoState]{} }), "ideoligion")
	on.fails(statedPawn(func(p *PawnState) { p.Body.Ideo = Some(IdeoState{Present: true, Precepts: []string{"Nope"}}) }), "precept def Nope")
	on.fails(statedPawn(func(p *PawnState) { p.Body.Ideo = Some(IdeoState{Present: true, Role: "Nope"}) }), "precept def Nope")
}

func TestBaseConditionalAffecterAppliesOnlyWithModifiers(t *testing.T) {
	// The precept loops skip an affecter whose list is null without asking
	// whether it applies, so a fact only that affecter reads is not required.
	p := &d.PreceptDef{DefName: "P", ConditionalStatAffecters: []*d.Opt_ConditionalStatAffecterAny{
		conditional("Child", nil, []*d.Opt_StatModifier{mod(testStat, 2)}),
	}}
	r := newBaseRig(t, baseOpts{biotech: true, defs: withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.PreceptDefs = append(s.PreceptDefs, p) })})
	ctx := statedPawn(func(ps *PawnState) { ps.Body.Ideo = Some(IdeoState{Present: true, Precepts: []string{"P"}}) })
	// Offsets pass: statOffsets null -> Applies not consulted. Factors pass:
	// Applies reads the developmental stage, not stated.
	r.fails(ctx, "developmental stage")
	ctx.Pawn.Base.Developmental = Some(devChild)
	checkValue(t, r.at(ctx), 20)
	ctx.Pawn.Base.Developmental = Some[int32](8)
	checkValue(t, r.at(ctx), 10)
}

func geneDef(name string, offsets, factors []*d.Opt_StatModifier, cond ...*d.Opt_ConditionalStatAffecterAny) *d.GeneDef {
	return &d.GeneDef{DefName: name, StatOffsets: offsets, StatFactors: factors, ConditionalStatAffecters: cond}
}

func TestBaseGenes(t *testing.T) {
	g1 := geneDef("G1", []*d.Opt_StatModifier{mod(testStat, 2)}, []*d.Opt_StatModifier{mod(testStat, 3)},
		conditional("Child", []*d.Opt_StatModifier{mod(testStat, 1)}, []*d.Opt_StatModifier{mod(testStat, 2)}))
	defs := withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.GeneDefs = append(s.GeneDefs, g1) })
	genes := func(dev int32, active ...string) *StatContext {
		return statedPawn(func(p *PawnState) {
			p.Body.Genes = Some(activeGenesOf(active...))
			p.Base.Developmental = Some(dev)
		})
	}
	r := newBaseRig(t, baseOpts{defs: defs, biotech: true})
	checkValue(t, r.at(genes(8, "G1")), 36) // (10+2)*3
	checkValue(t, r.at(genes(4, "G1")), 78) // (10+2+1)*3*2
	checkValue(t, r.at(genes(8)), 10)       // no active genes
	checkValue(t, r.at(statedPawn(func(p *PawnState) { p.Body.Genes = Some(GenesState{}) })), 10)
}

func TestBaseGenesNeedBiotechAndFacts(t *testing.T) {
	g1 := geneDef("G1", []*d.Opt_StatModifier{mod(testStat, 2)}, nil)
	defs := withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.GeneDefs = append(s.GeneDefs, g1) })
	off := newBaseRig(t, baseOpts{defs: defs})
	// Without Biotech genes are never read, so even an absent fact is fine.
	checkValue(t, off.at(statedPawn(func(p *PawnState) { p.Body.Genes = Known[GenesState]{} })), 10)
	on := newBaseRig(t, baseOpts{defs: defs, biotech: true})
	on.fails(statedPawn(func(p *PawnState) { p.Body.Genes = Known[GenesState]{} }), "genes")
	on.fails(statedPawn(func(p *PawnState) { p.Body.Genes = Some(activeGenesOf([]string{"Nope"}...)) }), "gene def Nope")
	// A gene's affecter is asked even with null lists.
	g2 := geneDef("G2", nil, nil, conditional("Child", nil, nil))
	asks := newBaseRig(t, baseOpts{biotech: true, defs: withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.GeneDefs = append(s.GeneDefs, g2) })})
	asks.fails(statedPawn(func(p *PawnState) { p.Body.Genes = Some(activeGenesOf([]string{"G2"}...)) }), "developmental stage")
}

func TestBaseLifeStage(t *testing.T) {
	r := newBaseRig(t, baseOpts{defs: lifeStages([]*d.Opt_StatModifier{mod(testStat, 4)}, []*d.Opt_StatModifier{mod(testStat, 0.5)})})
	checkValue(t, r.at(statedPawn(nil)), 7)
	r.fails(statedPawn(func(p *PawnState) { p.Body.CurLifeStage = Some("Nope") }), "life stage def Nope")
	r.fails(statedPawn(func(p *PawnState) { p.Body.CurLifeStage = Known[string]{} }), "life stage")
}

func TestBaseInspirationAndStatFactors(t *testing.T) {
	insp := &d.InspirationDef{DefName: "I", StatOffsets: []*d.Opt_StatModifier{mod(testStat, 2)}, StatFactors: []*d.Opt_StatModifier{mod(testStat, 3)}}
	r := newBaseRig(t, baseOpts{
		stat:  func(s *d.StatDef) { s.StatFactors = []string{otherStat} },
		parka: func(p *d.ThingDef) { p.StatBases = []*d.Opt_StatModifier{mod(otherStat, 0.5)} },
		defs:  withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.InspirationDefs = append(s.InspirationDefs, insp) }),
	})
	// statFactors first (x0.5), then inspiration's offset then factor.
	checkValue(t, r.at(statedPawn(nil)), 5)
	checkValue(t, r.at(statedPawn(func(p *PawnState) { p.Base.Inspiration = Some("I") })), 21)
	r.fails(statedPawn(func(p *PawnState) { p.Base.Inspiration = Some("Nope") }), "inspiration def Nope")
	r.fails(statedPawn(func(p *PawnState) { p.Base.Inspiration = Known[string]{} }), "inspiration")
	// A non-pawn thing takes only the statFactors.
	checkValue(t, r.at(&StatContext{}), 5)
}

func TestBaseOrderOfOperations(t *testing.T) {
	t1 := trait("T1", 0, []*d.Opt_StatModifier{mod(testStat, 1)}, []*d.Opt_StatModifier{mod(testStat, 3)})
	r := newBaseRig(t, baseOpts{
		stat: func(s *d.StatDef) {
			s.SkillNeedOffsets = []*d.Opt_SkillNeedAny{skillDirect("Mining", 2)}
			s.SkillNeedFactors = []*d.Opt_SkillNeedAny{skillDirect("Mining", 5)}
			s.StatFactors = []string{otherStat}
		},
		parka: func(p *d.ThingDef) { p.StatBases = []*d.Opt_StatModifier{mod(otherStat, 0.5)} },
		defs: withDefs(lifeStages([]*d.Opt_StatModifier{mod(testStat, 1)}, []*d.Opt_StatModifier{mod(testStat, 2)}),
			func(s *d.DefSets) { s.TraitDefs = append(s.TraitDefs, t1) }),
	})
	ctx := statedPawn(func(p *PawnState) {
		skillsAt(map[string]int32{"Mining": 0})(p)
		p.Base.Story = Some(StoryState{Present: true, Traits: []TraitState{{Def: "T1"}}})
	})
	// ((10 + skill 2 + trait 1 + lifestage 1) * trait 3 * lifestage 2) * statFactor 0.5 * skill 5
	checkValue(t, r.at(ctx), 14*3*2*0.5*5)
}

func gearRig(t *testing.T, stat func(*d.StatDef), extra func(*d.DefSets)) *rig {
	return newBaseRig(t, baseOpts{
		stat:   stat,
		parka:  func(p *d.ThingDef) { p.EquippedStatOffsets = []*d.Opt_StatModifier{mod(testStat, 2)} },
		steel:  func(s *d.ThingDef) { s.EquippedStatOffsets = []*d.Opt_StatModifier{mod(testStat, 3)} },
		chains: []string{"RimWorld.CompBladelinkWeapon"},
		defs:   withDefs(lifeStages(nil, nil), extra),
	})
}

func TestBaseGearOffsets(t *testing.T) {
	r := gearRig(t, nil, nil)
	both := statedPawn(func(p *PawnState) {
		wearing("Apparel_Parka")(p)
		primary := &GearPiece{Subject: ThingSubject("Steel", "")}
		p.Base.Primary = Some(primary)
	})
	checkValue(t, r.at(both), 15)
	checkValue(t, r.at(statedPawn(wearing("Apparel_Parka"))), 12)
	// A pawn without an apparel tracker wears nothing.
	checkValue(t, r.at(statedPawn(func(p *PawnState) { p.Base.Apparel = Some(WornGear{}) })), 10)
	r.fails(statedPawn(func(p *PawnState) { p.Base.Apparel = Known[WornGear]{} }), "worn apparel")
	r.fails(statedPawn(func(p *PawnState) { p.Base.Primary = Known[*GearPiece]{} }), "primary weapon")
	r.fails(statedPawn(wearing("Nope")), "thing def Nope")
}

func TestBaseGearRunsPartsOnTheGear(t *testing.T) {
	quality := &d.Opt_StatPartAny{Value: &d.StatPartAny{Value: &d.StatPartAny_StatPart_Quality_Offset{StatPart_Quality_Offset: &d.StatPart_Quality_Offset{
		OffsetNormal: 1, OffsetGood: 10,
	}}}}
	r := gearRig(t, func(s *d.StatDef) { s.Parts = []*d.Opt_StatPartAny{quality} }, nil)
	gear := func(ctx *StatContext) func(*PawnState) {
		return func(p *PawnState) {
			p.Base.Apparel = Some(WornGear{Present: true, Worn: []GearPiece{{Subject: Subject{Def: "Apparel_Parka", Context: ctx}}}})
		}
	}
	normal := &StatContext{Base: BaseFacts{Quality: Some(QualityState{Has: true, Category: 2})}}
	good := &StatContext{Base: BaseFacts{Quality: Some(QualityState{Has: true, Category: 3})}}
	// The gear's 2 becomes 2+1; the pawn's total gets the pawn's own +1.
	checkValue(t, r.at(statedPawn(gear(normal))), 10+3+1)
	checkValue(t, r.at(statedPawn(gear(good))), 10+12+1)
	r.fails(statedPawn(gear(nil)), "thing context of Apparel_Parka")
	r.fails(statedPawn(gear(&StatContext{})), "thing's quality")
	// A zero total never reaches the parts, so no gear context is needed.
	zero := newBaseRig(t, baseOpts{stat: func(s *d.StatDef) { s.Parts = []*d.Opt_StatPartAny{quality} }, defs: lifeStages(nil, nil)})
	checkValue(t, zero.at(statedPawn(gear(nil))), 11)
}

func bladelink() *d.Opt_CompPropertiesAny {
	return &d.Opt_CompPropertiesAny{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_BladelinkWeapon{
		CompProperties_BladelinkWeapon: &d.CompProperties_BladelinkWeapon{CompClass: classCompBladelink}}}}
}

func TestBaseGearBladelinkTraits(t *testing.T) {
	wt := &d.WeaponTraitDef{DefName: "WT", EquippedStatOffsets: []*d.Opt_StatModifier{mod(testStat, 5)}}
	r := newBaseRig(t, baseOpts{
		parka: func(p *d.ThingDef) {
			p.EquippedStatOffsets = []*d.Opt_StatModifier{mod(testStat, 2)}
			p.Comps = []*d.Opt_CompPropertiesAny{bladelink()}
		},
		chains: []string{classCompBladelink},
		defs:   withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.WeaponTraitDefs = append(s.WeaponTraitDefs, wt) }),
	})
	piece := func(traits Known[[]string]) func(*PawnState) {
		return func(p *PawnState) {
			p.Base.Apparel = Some(WornGear{Present: true, Worn: []GearPiece{{Subject: ThingSubject("Apparel_Parka", ""), BladelinkTraits: traits}}})
		}
	}
	checkValue(t, r.at(statedPawn(piece(Some([]string{"WT", "WT"})))), 10+2+5+5)
	checkValue(t, r.at(statedPawn(piece(Some([]string{})))), 12)
	r.fails(statedPawn(piece(Known[[]string]{})), "bladelink traits of Apparel_Parka")
	r.fails(statedPawn(piece(Some([]string{"Nope"}))), "weapon trait def Nope")
}

func TestBaseStuffQualityTerms(t *testing.T) {
	r := newBaseRig(t, baseOpts{
		steel: func(s *d.ThingDef) {
			s.StuffProps.StatFactors = nil
			s.StuffProps.StatOffsets = nil
			s.StuffProps.StatFactorsQuality = []*d.Opt_StatModifierQuality{{Value: &d.StatModifierQuality{Stat: testStat, Normal: 1.5, Good: 2}}}
			s.StuffProps.StatOffsetsQuality = []*d.Opt_StatModifierQuality{{Value: &d.StatModifierQuality{Stat: testStat, Normal: 1, Good: 0.5}}}
		},
	})
	on := func(q QualityState) float32 {
		subject := ThingSubject("Apparel_Parka", "Steel")
		subject.Context = &StatContext{Base: BaseFacts{Quality: Some(q)}}
		v, err := r.eval.Value(testStat, subject)
		if err != nil {
			t.Fatal(err)
		}
		return v
	}
	checkValue(t, on(QualityState{Has: true, Category: 2}), 10*1.5+1)
	checkValue(t, on(QualityState{Has: true, Category: 3}), 10*2+0.5)
	// A thing without a CompQuality takes the plain stuff terms only.
	checkValue(t, on(QualityState{}), 10)
	// Stuff without a quality fact is an error; so is a definition request's
	// quality given a thing request.
	subject := ThingSubject("Apparel_Parka", "Steel")
	subject.Context = &StatContext{}
	if _, err := r.eval.Value(testStat, subject); err == nil || !strings.Contains(err.Error(), "thing's quality") {
		t.Errorf("error = %v, want the quality fact named", err)
	}
	// A definition request reads neither quality list.
	checkValue(t, mustValue(t, r.eval, ThingSubject("Apparel_Parka", "Steel")), 10)
	q := int32(3)
	both := subject
	both.Quality = &q
	if _, err := r.eval.Value(testStat, both); err == nil || !strings.Contains(err.Error(), "Subject.Quality") {
		t.Errorf("error = %v, want Subject.Quality refused", err)
	}
}

func mustValue(t *testing.T, e *Evaluator, s Subject) float32 {
	t.Helper()
	v, err := e.Value(testStat, s)
	if err != nil {
		t.Fatal(err)
	}
	return v
}

func compProps(class string) *d.Opt_CompPropertiesAny {
	any := &d.CompPropertiesAny{}
	switch class {
	case classCompUniqueWeapon:
		any.Value = &d.CompPropertiesAny_CompProperties_UniqueWeapon{CompProperties_UniqueWeapon: &d.CompProperties_UniqueWeapon{CompClass: class}}
	case classCompFacilities:
		any.Value = &d.CompPropertiesAny_CompProperties_AffectedByFacilities{CompProperties_AffectedByFacilities: &d.CompProperties_AffectedByFacilities{CompClass: class}}
	case classCompBiocodable:
		any.Value = &d.CompPropertiesAny_CompProperties_Biocodable{CompProperties_Biocodable: &d.CompProperties_Biocodable{CompClass: class}}
	}
	return &d.Opt_CompPropertiesAny{Value: any}
}

func TestBaseCompStatOverrides(t *testing.T) {
	wt1 := &d.WeaponTraitDef{DefName: "WT1", StatOffsets: []*d.Opt_StatModifier{mod(testStat, 2)}, StatFactors: []*d.Opt_StatModifier{mod(testStat, 2)}}
	wt2 := &d.WeaponTraitDef{DefName: "WT2", StatOffsets: []*d.Opt_StatModifier{mod(testStat, 1)}, StatFactors: []*d.Opt_StatModifier{mod(testStat, 3)}}
	defs := withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.WeaponTraitDefs = append(s.WeaponTraitDefs, wt1, wt2) })
	chains := []string{classCompUniqueWeapon, classCompFacilities}

	unique := newBaseRig(t, baseOpts{parka: func(p *d.ThingDef) { p.Comps = []*d.Opt_CompPropertiesAny{compProps(classCompUniqueWeapon)} }, chains: chains, defs: defs})
	traits := func(names ...string) *StatContext {
		return &StatContext{Base: BaseFacts{UniqueWeaponTraits: Some(names)}}
	}
	// Offsets 2+1 first, then factors 2*3: (10+3)*6.
	checkValue(t, unique.at(traits("WT1", "WT2")), 78)
	checkValue(t, unique.at(traits()), 10)
	unique.fails(&StatContext{}, "weapon traits")
	unique.fails(traits("Nope"), "weapon trait def Nope")

	fac := newBaseRig(t, baseOpts{parka: func(p *d.ThingDef) { p.Comps = []*d.Opt_CompPropertiesAny{compProps(classCompFacilities)} }, chains: chains, defs: defs})
	links := func(l ...FacilityLink) *StatContext {
		return &StatContext{Base: BaseFacts{Facilities: Some(l)}}
	}
	active := FacilityLink{StatOffsets: []StatMod{{Stat: otherStat, Value: 9}, {Stat: testStat, Value: 2}}, Active: true}
	inactive := FacilityLink{StatOffsets: []StatMod{{Stat: testStat, Value: 4}}}
	nullList := FacilityLink{Active: true}
	zero := FacilityLink{StatOffsets: []StatMod{{Stat: testStat, Value: 0}}, Active: true}
	checkValue(t, fac.at(links(active, inactive, nullList, zero, active)), 14)
	checkValue(t, fac.at(links()), 10)
	fac.fails(&StatContext{}, "linked facilities")

	// Comps that override neither contribute nothing, and need no fact.
	plain := newBaseRig(t, baseOpts{parka: func(p *d.ThingDef) { p.Comps = []*d.Opt_CompPropertiesAny{compProps(classCompBiocodable)} }, chains: []string{classCompBiocodable}, defs: defs})
	checkValue(t, plain.at(&StatContext{}), 10)
}

func TestBaseMinifiedThingInherits(t *testing.T) {
	inner := ThingSubject("Steel", "")
	inner.Context = &StatContext{}
	minified := func(stat bool) *rig {
		return newBaseRig(t, baseOpts{
			stat: func(s *d.StatDef) { s.MinifiedThingInherits = stat },
			parka: func(p *d.ThingDef) {
				p.ThingClass = classMinifiedThing
				p.StatBases = []*d.Opt_StatModifier{mod(testStat, 1)}
			},
			steel:  func(s *d.ThingDef) { s.StatBases = []*d.Opt_StatModifier{mod(testStat, 7)} },
			chains: []string{classMinifiedThing},
		})
	}
	r := minified(true)
	checkValue(t, r.at(&StatContext{Base: BaseFacts{Minified: Some(&inner)}}), 7)
	// A null inner thing evaluates the minified thing itself.
	checkValue(t, r.at(&StatContext{Base: BaseFacts{Minified: Some[*Subject](nil)}}), 1)
	r.fails(&StatContext{}, "inner thing")
	// Without minifiedThingInherits the inner thing is not asked for.
	checkValue(t, minified(false).at(&StatContext{}), 1)
}

func TestBasePostProcessStatFactorsAndNoPostProcess(t *testing.T) {
	r := newBaseRig(t, baseOpts{
		stat: func(s *d.StatDef) {
			s.PostProcessCurve = curve(0, 0, 100, 50)
			s.PostProcessStatFactors = []string{otherStat}
			s.MaxValue = 4
		},
		parka: func(p *d.ThingDef) { p.StatBases = []*d.Opt_StatModifier{mod(otherStat, 0.5)} },
	})
	// Curve(10) = 5, x0.5 = 2.5, within the max.
	checkValue(t, r.at(&StatContext{}), 2.5)
	// A definition request has no thing: no factors (and Other has none).
	checkValue(t, mustValue(t, r.eval, ThingSubject("Apparel_Parka", "")), 4)
	// Without the post-process the value is the raw 10, unclamped.
	v, err := r.eval.ValueWithoutPostProcess(testStat, onThing(&StatContext{}))
	if err != nil {
		t.Fatal(err)
	}
	checkValue(t, v, 10)
	v, err = r.eval.ValueWithoutPostProcess(testStat, ThingSubject("Apparel_Parka", ""))
	if err != nil {
		t.Fatal(err)
	}
	checkValue(t, v, 10)
}

// activeGenesOf is a gene tracker holding the named genes, none overridden.
func activeGenesOf(names ...string) GenesState {
	g := GenesState{Present: true}
	for _, n := range names {
		g.Genes = append(g.Genes, GeneState{Def: n})
	}
	return g
}
