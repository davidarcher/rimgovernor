package stateval

import (
	"strings"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// A thing request is answered from its StatContext; a terrain takes none.
func TestThingRequestIsAnswered(t *testing.T) {
	r := newBaseRig(t, baseOpts{parka: func(p *d.ThingDef) { p.StatBases = []*d.Opt_StatModifier{mod(testStat, 4)} }})
	subject := onThing(&StatContext{})
	v, err := r.eval.Value(testStat, subject)
	if err != nil {
		t.Fatal(err)
	}
	checkValue(t, v, 4)
	shown, err := r.eval.ShouldShowFor(testStat, subject)
	if err != nil || !shown {
		t.Fatalf("shown = %v, %v", shown, err)
	}
	res, err := r.eval.Evaluate(testStat, subject)
	if err != nil || res.Value != 4 || !res.Shown {
		t.Fatalf("Evaluate = %+v, %v", res, err)
	}
	terrain := TerrainSubject("Soil")
	terrain.Context = &StatContext{}
	if _, err := r.eval.request(testStat, terrain); err == nil {
		t.Error("a terrain with a thing context was accepted")
	}
}

// showRig is a humanlike pawn def under a stat of the BasicsPawn category
// that shows on every kind of pawn; edit narrows it.
func showRig(t *testing.T, edit func(*d.StatDef), parka func(*d.ThingDef), biotech bool) *rig {
	return newBaseRig(t, baseOpts{
		biotech: biotech,
		stat: func(s *d.StatDef) {
			s.Category = catBasicsPawn
			s.ShowOnPawns, s.ShowOnHumanlikes, s.ShowOnNonWildManHumanlikes = true, true, true
			s.ShowOnAnimals, s.ShowOnMechanoids, s.ShowOnEntities, s.ShowOnDrones = true, true, true, true
			s.ShowDevelopmentalStageFilter = d.DevelopmentalStage(14)
			if edit != nil {
				edit(s)
			}
		},
		parka: func(p *d.ThingDef) {
			p.Category = d.ThingCategory_THING_CATEGORY_PAWN
			p.Tradeability = d.Tradeability_TRADEABILITY_NONE
			if parka != nil {
				parka(p)
			}
		},
	})
}

func (r *rig) shownOn(ctx *StatContext) (bool, error) {
	return r.eval.ShouldShowFor(testStat, onThing(ctx))
}

func wantShown(t *testing.T, r *rig, ctx *StatContext, want bool) {
	t.Helper()
	got, err := r.shownOn(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if got != want {
		t.Errorf("shown = %v, want %v", got, want)
	}
}

func wantShowErr(t *testing.T, r *rig, ctx *StatContext, want string) {
	t.Helper()
	if _, err := r.shownOn(ctx); err == nil || !strings.Contains(err.Error(), want) {
		t.Fatalf("error = %v, want one containing %q", err, want)
	}
}

func pawnOnly(edit func(*PawnState)) *StatContext {
	p := &PawnState{}
	p.Base.Developmental = Some[int32](8)
	if edit != nil {
		edit(p)
	}
	return &StatContext{Pawn: p}
}

func TestShowIfHediffsPresent(t *testing.T) {
	r := showRig(t, func(s *d.StatDef) { s.ShowIfHediffsPresent = []string{"H1", "H2"} }, nil, false)
	has := func(defs ...string) *StatContext {
		return pawnOnly(func(p *PawnState) {
			hs := []HediffState{}
			for _, def := range defs {
				hs = append(hs, HediffState{Def: def})
			}
			p.Body.Hediffs = Some(hs)
		})
	}
	wantShown(t, r, has("H1"), false)
	wantShown(t, r, has("H2", "Other", "H1"), true)
	wantShown(t, r, has(), false)
	wantShowErr(t, r, pawnOnly(nil), "pawn's hediffs")
	// A definition request has no pawn: the list does not apply.
	got, err := r.eval.ShouldShowFor(testStat, ThingSubject("Apparel_Parka", ""))
	if err != nil || !got {
		t.Errorf("definition request shown = %v, %v", got, err)
	}
	// A thing that is not a Pawn is not asked either.
	wantShown(t, r, &StatContext{}, true)
}

func TestShowOnSlavesOnly(t *testing.T) {
	r := showRig(t, func(s *d.StatDef) { s.ShowOnSlavesOnly = true }, nil, false)
	wantShown(t, r, pawnOnly(func(p *PawnState) { p.IsSlave = Some(true) }), true)
	wantShown(t, r, pawnOnly(func(p *PawnState) { p.IsSlave = Some(false) }), false)
	wantShowErr(t, r, pawnOnly(nil), "slave")
}

func TestShowMaxHitPointsHidesOnAThing(t *testing.T) {
	r := newBaseRig(t, baseOpts{stat: func(s *d.StatDef) { s.DefName = statMaxHitPoints }})
	hidden, err := r.eval.ShouldShowFor(statMaxHitPoints, onThing(&StatContext{}))
	if err != nil || hidden {
		t.Errorf("MaxHitPoints of a thing: shown = %v, %v", hidden, err)
	}
	def, err := r.eval.ShouldShowFor(statMaxHitPoints, ThingSubject("Apparel_Parka", ""))
	if err != nil || !def {
		t.Errorf("MaxHitPoints of a def: shown = %v, %v", def, err)
	}
}

func TestShowOnNonWildManHumanlikes(t *testing.T) {
	r := showRig(t, func(s *d.StatDef) { s.ShowOnNonWildManHumanlikes = false }, nil, false)
	wantShown(t, r, pawnOnly(func(p *PawnState) { p.IsWildMan = Some(true) }), true)
	wantShown(t, r, pawnOnly(func(p *PawnState) { p.IsWildMan = Some(false) }), false)
	wantShowErr(t, r, pawnOnly(nil), "wild man")
	// No pawn at all (a definition request, or a non-Pawn thing) is never one.
	wantShown(t, r, &StatContext{}, false)
	got, err := r.eval.ShouldShowFor(testStat, ThingSubject("Apparel_Parka", ""))
	if err != nil || got {
		t.Errorf("definition request shown = %v, %v", got, err)
	}
}

func TestShowDevelopmentalStageFilter(t *testing.T) {
	r := showRig(t, func(s *d.StatDef) { s.ShowDevelopmentalStageFilter = d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT }, nil, false)
	stage := func(v int32) *StatContext { return pawnOnly(func(p *PawnState) { p.Base.Developmental = Some(v) }) }
	wantShown(t, r, stage(8), true)
	wantShown(t, r, stage(4), false)
	wantShown(t, r, stage(1), false)
	wantShowErr(t, r, &StatContext{Pawn: &PawnState{}}, "developmental stage")
	// The filter reads the pawn only: a definition request passes.
	got, err := r.eval.ShouldShowFor(testStat, ThingSubject("Apparel_Parka", ""))
	if err != nil || !got {
		t.Errorf("definition request shown = %v, %v", got, err)
	}
}

func TestShowPlayerMechanoidsAndPawnKinds(t *testing.T) {
	animal := func(p *d.ThingDef) {
		p.Race = &d.RaceProperties{Intelligence: d.Intelligence_INTELLIGENCE_ANIMAL, LifeExpectancy: 10}
	}
	r := showRig(t, func(s *d.StatDef) {
		s.Category = catPawnMisc
		s.ShowOnPlayerMechanoids = true
		s.ShowOnPawnKind = []string{"Kind1"}
	}, animal, false)
	mech := func(v bool, kind Known[string]) *StatContext {
		return pawnOnly(func(p *PawnState) { p.Base.IsColonyMech = Some(v); p.Base.KindDef = kind })
	}
	wantShown(t, r, mech(true, Known[string]{}), true)
	wantShown(t, r, mech(false, Some("Kind1")), true)
	wantShown(t, r, mech(false, Some("Kind2")), false)
	wantShowErr(t, r, mech(false, Known[string]{}), "kind def")
	wantShowErr(t, r, pawnOnly(nil), "colony mech")
	// Without the mechanoid flag only the kind list matters.
	noMech := showRig(t, func(s *d.StatDef) { s.Category = catPawnMisc; s.ShowOnPawnKind = []string{"Kind1"} }, animal, false)
	wantShown(t, noMech, pawnOnly(func(p *PawnState) { p.Base.KindDef = Some("Kind1") }), true)
	// Without a list the kind is not asked; humanlikes fall through to true.
	human := showRig(t, func(s *d.StatDef) { s.Category = catPawnMisc }, nil, false)
	wantShown(t, human, pawnOnly(nil), true)
	// A definition request has no pawn: only the humanlike rule.
	got, err := r.eval.ShouldShowFor(testStat, ThingSubject("Apparel_Parka", ""))
	if err != nil || got {
		t.Errorf("definition request shown = %v, %v", got, err)
	}
}

func TestShowDisplayTradeStatsForAThing(t *testing.T) {
	// A pawn def with no tradeability is untradeable unless a colony mech.
	pawnRig := func(biotech bool) *rig {
		return showRig(t, func(s *d.StatDef) { s.ShowOnUntradeables = false }, nil, biotech)
	}
	mech := func(v bool) *StatContext {
		return pawnOnly(func(p *PawnState) { p.Base.IsColonyMech = Some(v) })
	}
	wantShown(t, pawnRig(true), mech(true), true)
	wantShown(t, pawnRig(true), mech(false), false)
	wantShowErr(t, pawnRig(true), pawnOnly(nil), "colony mech")
	// Without Biotech colony mechs are not special (and not asked about).
	wantShown(t, pawnRig(false), pawnOnly(nil), false)

	// A minifiable building is tradeable unless it is biocoded.
	building := func(p *d.ThingDef) {
		p.Category = d.ThingCategory_THING_CATEGORY_BUILDING
		p.MinifiedDef = "Steel"
		p.Comps = []*d.Opt_CompPropertiesAny{compProps(classCompBiocodable)}
	}
	br := newBaseRig(t, baseOpts{
		stat:   func(s *d.StatDef) { s.ShowOnUntradeables = false },
		parka:  building,
		chains: []string{classCompBiocodable},
	})
	coded := func(v Known[bool]) *StatContext { return &StatContext{Base: BaseFacts{Biocoded: v}} }
	wantShown(t, br, coded(Some(false)), true)
	wantShown(t, br, coded(Some(true)), false)
	wantShowErr(t, br, coded(Known[bool]{}), "biocoded")
	// A def without the comp never asks.
	plain := newBaseRig(t, baseOpts{
		stat:  func(s *d.StatDef) { s.ShowOnUntradeables = false },
		parka: func(p *d.ThingDef) { building(p); p.Comps = nil },
	})
	wantShown(t, plain, &StatContext{}, true)
}
