package stateval

import (
	"fmt"
	"math"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// The pawn-group StatParts (epic #2621, #2639). A definition request has no
// thing, so every part that reads the thing leaves the value alone; the ones
// that read PawnOrCorpseStatUtility also answer for a pawn or corpse def.
// Only StatPart_Trainable overrides ForceShow.

func pawnPartList() []Part {
	return []Part{
		partBodySize{}, partIsFlesh{}, partLifeStageMaxFood{}, partMetabolismTotal{},
		partNaturalNotMissingBodyPartsCoverage{}, partNotCarefullySlaughtered{}, partAddedBodyPartsMass{},
		partTrainable{}, partTerrorFall{}, partShamblerCorpse{}, partShamblerCrawling{}, partRevenantSpeed{},
		partTerrainMoveSpeed{}, partBlindPsychicSensitivityOffset{}, partSightPsychicSensitivityOffset{},
		partOverseerStatOffset{}, partRoleConversionPower{}, partPlayerFactionLeader{}, partBedStat{},
		partBiosculptingSpeedFactor{}, partGrowthVatSpeedFactor{},
	}
}

// Game names the parts test against.
const (
	classHediffInjury   = "Verse.Hediff_Injury"
	classHediffAddedPar = "Verse.Hediff_AddedPart"

	hediffExecutionCut   = "ExecutionCut"   // HediffDefOf.ExecutionCut
	hediffShamblerCorpse = "ShamblerCorpse" // HediffDefOf.ShamblerCorpse
	kindRevenant         = "Revenant"       // PawnKindDefOf.Revenant
	statMass             = "Mass"           // StatDefOf.Mass
)

// suppressionFallRateOverTerror is TerrorUtility.SuppressionFallRateOverTerror.
// It is retyped here because it is a static-initializer-built SimpleCurve that
// the GameConstants generator cannot carry (it holds only TerrorUtility's two
// ints); this is its one copy. Delete it when the generator emits it.
var suppressionFallRateOverTerror = curveOf(0, 0, 25, -15, 50, -25, 100, -45)

func curveOf(points ...float32) *d.SimpleCurve {
	c := &d.SimpleCurve{}
	for i := 0; i < len(points); i += 2 {
		c.Points = append(c.Points, &d.CurvePoint{Loc: &d.Vector2{X: points[i], Y: points[i+1]}})
	}
	return c
}

// approximately is Mathf.Approximately (Mathf.Epsilon is the smallest
// denormal).
func approximately(a, b float32) bool {
	tol := float32(1e-6 * max(abs32(a), abs32(b)))
	if floor := float32(8 * math.SmallestNonzeroFloat32); tol < floor {
		tol = floor
	}
	return abs32(float32(b-a)) < tol
}

// lerpDoubleClamped is GenMath.LerpDoubleClamped.
func lerpDoubleClamped(inFrom, inTo, outFrom, outTo, x float32) float32 {
	x = clamp32(x, min(inFrom, inTo), max(inFrom, inTo))
	num := float32(float32(x-inFrom) / float32(inTo-inFrom))
	return float32(outFrom + float32(float32(outTo-outFrom)*num))
}

// pawnOrCorpse is PawnOrCorpseStatUtility.TryGetPawnOrCorpseStat: a thing
// request answers for a pawn or a corpse's inner pawn (fromPawn gets its
// state and its def row), a definition request for a pawn def or a corpse
// def's source race (fromDef); anything else leaves the value alone.
func pawnOrCorpse(req *Request, fromPawn func(*PawnState, *d.ThingDef) (float32, error), fromDef func(*d.ThingDef) (float32, error)) (float32, bool, error) {
	if ctx := req.Subject.Context; ctx != nil {
		switch {
		case ctx.Pawn != nil:
			v, err := fromPawn(ctx.Pawn, req.Thing)
			return v, err == nil, err
		case ctx.Corpse != nil:
			src, err := corpseSource(req)
			if err != nil {
				return 0, false, err
			}
			v, err := fromPawn(ctx.Corpse, src)
			return v, err == nil, err
		}
		return 0, false, nil
	}
	if req.Thing == nil {
		return 0, false, nil
	}
	if req.Thing.GetCategory() == d.ThingCategory_THING_CATEGORY_PAWN {
		v, err := fromDef(req.Thing)
		return v, err == nil, err
	}
	corpse, err := req.Evaluator.classIsA(req.Thing.GetThingClass(), classCorpse)
	if err != nil || !corpse {
		return 0, false, err
	}
	src, err := corpseSource(req)
	if err != nil {
		return 0, false, err
	}
	v, err := fromDef(src)
	return v, err == nil, err
}

// corpseSource is the corpse def's ingestible.sourceDef: the race of the
// inner pawn.
func corpseSource(req *Request) (*d.ThingDef, error) {
	name := req.Thing.GetIngestible().GetSourceDef()
	if name == "" {
		return nil, fmt.Errorf("corpse def %s has no ingestible source def", req.Subject.Def)
	}
	src := req.Evaluator.catalog.ThingDef(name)
	if src == nil {
		return nil, fmt.Errorf("catalog has no thing def %s (source of corpse %s)", name, req.Subject.Def)
	}
	return src, nil
}

// biotechPawn is the common head of the Biotech parts: a thing request's pawn,
// or nil when the part does not apply (a definition request, a non-pawn, or
// Biotech inactive).
func biotechPawn(req *Request) (*PawnState, error) {
	if req.Subject.Context == nil {
		return nil, nil
	}
	biotech, err := req.Evaluator.modActive(modBiotech)
	if err != nil || !biotech {
		return nil, err
	}
	return req.pawn(), nil
}

// lifeStage is the LifeStageDef row of the pawn's current life stage.
func lifeStage(req *Request, pawn *PawnState) (*d.LifeStageDef, error) {
	name, err := need(pawn.Body.CurLifeStage, "the pawn's current life stage")
	if err != nil {
		return nil, err
	}
	row := DefRow[*d.LifeStageDef](req.Evaluator.catalog, name)
	if row == nil {
		return nil, fmt.Errorf("catalog has no life stage def %s", name)
	}
	return row, nil
}

// --- body size, flesh, life stage ---

// pawnBodySize is Pawn.BodySize: the current life stage's bodySizeFactor times
// the race's baseBodySize.
func pawnBodySize(req *Request, pawn *PawnState, def *d.ThingDef) (float32, error) {
	stage, err := lifeStage(req, pawn)
	if err != nil {
		return 0, err
	}
	return mul(stage.GetBodySizeFactor(), def.GetRace().GetBaseBodySize()), nil
}

// partBodySize is StatPart_BodySize: multiply by Pawn.BodySize (life stage
// factor times the race's base), or a def's race.baseBodySize.
type partBodySize struct{ plain }

func (partBodySize) Class() string { return "StatPart_BodySize" }

func (partBodySize) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	size, ok, err := pawnOrCorpse(req,
		func(p *PawnState, def *d.ThingDef) (float32, error) {
			return pawnBodySize(req, p, def)
		},
		func(def *d.ThingDef) (float32, error) { return def.GetRace().GetBaseBodySize(), nil })
	if err != nil || !ok {
		return val, err
	}
	return mul(val, size), nil
}

// partIsFlesh is StatPart_IsFlesh: 0 for a race whose flesh type is not
// organic, else 1.
type partIsFlesh struct{ plain }

func (partIsFlesh) Class() string { return "StatPart_IsFlesh" }

func (partIsFlesh) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	isFlesh := func(def *d.ThingDef) (float32, error) {
		flesh := DefRow[*d.FleshTypeDef](req.Evaluator.catalog, fleshType(def.GetRace()))
		if flesh == nil {
			return 0, fmt.Errorf("catalog has no flesh type %s", fleshType(def.GetRace()))
		}
		if flesh.GetIsOrganic() {
			return 1, nil
		}
		return 0, nil
	}
	factor, ok, err := pawnOrCorpse(req, func(_ *PawnState, def *d.ThingDef) (float32, error) { return isFlesh(def) }, isFlesh)
	if err != nil || !ok {
		return val, err
	}
	return mul(val, factor), nil
}

// partLifeStageMaxFood is StatPart_LifeStageMaxFood: multiply by the current
// life stage's foodMaxFactor.
type partLifeStageMaxFood struct{ plain }

func (partLifeStageMaxFood) Class() string { return "StatPart_LifeStageMaxFood" }

func (partLifeStageMaxFood) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	stage, err := lifeStage(req, pawn)
	if err != nil {
		return 0, err
	}
	return mul(val, stage.GetFoodMaxFactor()), nil
}

// partMetabolismTotal is StatPart_MetabolismTotal (a StatPart_BiostatTotal):
// the curve over the sum of the non-overridden genes' biostatMet, Biotech
// only and only for a pawn with a gene tracker.
type partMetabolismTotal struct{ plain }

func (partMetabolismTotal) Class() string { return "StatPart_MetabolismTotal" }

func (partMetabolismTotal) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_MetabolismTotal](row, "StatPart_MetabolismTotal")
	if err != nil {
		return 0, err
	}
	pawn, err := biotechPawn(req)
	if err != nil || pawn == nil {
		return val, err
	}
	genes, err := need(pawn.Body.Genes, "the pawn's genes")
	if err != nil || !genes.Present {
		return val, err
	}
	var total int32
	for _, g := range genes.Genes {
		if g.Overridden {
			continue
		}
		def := DefRow[*d.GeneDef](req.Evaluator.catalog, g.Def)
		if def == nil {
			return 0, fmt.Errorf("catalog has no gene def %s", g.Def)
		}
		total += def.GetBiostatMet()
	}
	factor, err := EvaluateCurve(p.GetCurve(), float32(total))
	if err != nil {
		return 0, err
	}
	return mul(val, factor), nil
}

// --- health ---

// partNaturalNotMissingBodyPartsCoverage is
// StatPart_NaturalNotMissingBodyPartsCoverage: the pawn's coverage of
// not-missing natural parts; 1 for a def.
type partNaturalNotMissingBodyPartsCoverage struct{ plain }

func (partNaturalNotMissingBodyPartsCoverage) Class() string {
	return "StatPart_NaturalNotMissingBodyPartsCoverage"
}

func (partNaturalNotMissingBodyPartsCoverage) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	coverage, ok, err := pawnOrCorpse(req,
		func(p *PawnState, _ *d.ThingDef) (float32, error) {
			return need(p.Body.NaturalCoverage, "the pawn's coverage of not-missing natural body parts")
		},
		func(*d.ThingDef) (float32, error) { return 1, nil })
	if err != nil || !ok {
		return val, err
	}
	return mul(val, coverage), nil
}

// hediffDef is the HediffDef row of a hediff in the pawn's set.
func hediffDef(req *Request, name string) (*d.HediffDef, error) {
	row := DefRow[*d.HediffDef](req.Evaluator.catalog, name)
	if row == nil {
		return nil, fmt.Errorf("catalog has no hediff def %s", name)
	}
	return row, nil
}

// partNotCarefullySlaughtered is StatPart_NotCarefullySlaughtered: the factor
// applies to a pawn with a non-permanent injury other than an execution cut.
type partNotCarefullySlaughtered struct{ plain }

func (partNotCarefullySlaughtered) Class() string { return "StatPart_NotCarefullySlaughtered" }

func (partNotCarefullySlaughtered) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_NotCarefullySlaughtered](row, "StatPart_NotCarefullySlaughtered")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	hediffs, err := need(pawn.Body.Hediffs, "the pawn's hediffs")
	if err != nil {
		return 0, err
	}
	for _, h := range hediffs {
		if h.Def == hediffExecutionCut {
			continue
		}
		def, err := hediffDef(req, h.Def)
		if err != nil {
			return 0, err
		}
		injury, err := req.Evaluator.classIsA(def.GetHediffClass(), classHediffInjury)
		if err != nil {
			return 0, err
		}
		if injury && !h.Permanent {
			return mul(val, p.GetFactor()), nil
		}
	}
	return val, nil
}

// partAddedBodyPartsMass is StatPart_AddedBodyPartsMass: add 0.9 of the mass
// of what each added part would spawn when removed; 0 for a def.
type partAddedBodyPartsMass struct{ plain }

func (partAddedBodyPartsMass) Class() string { return "StatPart_AddedBodyPartsMass" }

func (partAddedBodyPartsMass) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	mass, ok, err := pawnOrCorpse(req,
		func(p *PawnState, _ *d.ThingDef) (float32, error) {
			hediffs, err := need(p.Body.Hediffs, "the pawn's hediffs")
			if err != nil {
				return 0, err
			}
			constants, err := req.Evaluator.catalog.GameConstants()
			if err != nil {
				return 0, err
			}
			factor := constants.GetStatPart_AddedBodyPartsMass().GetAddedBodyPartMassFactor()
			var total float32
			for _, h := range hediffs {
				def, err := hediffDef(req, h.Def)
				if err != nil {
					return 0, err
				}
				added, err := req.Evaluator.classIsA(def.GetHediffClass(), classHediffAddedPar)
				if err != nil {
					return 0, err
				}
				if !added || def.GetSpawnThingOnRemoved() == "" {
					continue
				}
				m, err := req.Evaluator.Value(statMass, ThingSubject(def.GetSpawnThingOnRemoved(), ""))
				if err != nil {
					return 0, err
				}
				total = float32(total + float32(m*factor))
			}
			return total, nil
		},
		func(*d.ThingDef) (float32, error) { return 0, nil })
	if err != nil || !ok {
		return val, err
	}
	return float32(val + mass), nil
}

// --- animals, entities ---

// partTrainable is StatPart_Trainable: the factor applies to an animal that
// has learned Dig; ForceShow is the same predicate.
type partTrainable struct{}

func (partTrainable) Class() string { return "StatPart_Trainable" }

func trainableApplies(req *Request) (bool, error) {
	pawn := req.pawn()
	if pawn == nil {
		return false, nil
	}
	r, err := race(req)
	if err != nil {
		return false, err
	}
	entity, err := req.Evaluator.anomalyEntity(fleshType(r))
	if err != nil {
		return false, err
	}
	animal, err := req.Evaluator.animal(r, entity)
	if err != nil || !animal {
		return false, err
	}
	subhuman, err := need(pawn.Body.Subhuman, "whether the pawn is subhuman")
	if err != nil || subhuman {
		return false, err
	}
	return need(pawn.Body.DigLearned, "whether the pawn has learned Dig")
}

func (partTrainable) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Trainable](row, "StatPart_Trainable")
	if err != nil {
		return 0, err
	}
	applies, err := trainableApplies(req)
	if err != nil || !applies {
		return val, err
	}
	return mul(val, p.GetFactor()), nil
}

func (partTrainable) ForceShow(req *Request, _ proto.Message) (bool, error) {
	return trainableApplies(req)
}

// partTerrorFall is StatPart_Terror: add the suppression fall rate at the pawn's
// Terror stat.
type partTerrorFall struct{ plain }

func (partTerrorFall) Class() string { return "StatPart_Terror" }

func (partTerrorFall) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	terror, err := need(pawn.Body.Terror, "the pawn's Terror stat")
	if err != nil {
		return 0, err
	}
	rate, err := EvaluateCurve(suppressionFallRateOverTerror, terror)
	if err != nil {
		return 0, err
	}
	return float32(val + rate), nil
}

// partShamblerCorpse is StatPart_ShamblerCorpse: the multiplier applies to a
// corpse whose pawn has the ShamblerCorpse hediff (Anomaly only).
type partShamblerCorpse struct{ plain }

func (partShamblerCorpse) Class() string { return "StatPart_ShamblerCorpse" }

func (partShamblerCorpse) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_ShamblerCorpse](row, "StatPart_ShamblerCorpse")
	if err != nil {
		return 0, err
	}
	ctx := req.Subject.Context
	if ctx == nil {
		return val, nil
	}
	anomaly, err := req.Evaluator.modActive(modAnomaly)
	if err != nil || !anomaly || ctx.Corpse == nil {
		return val, err
	}
	hediffs, err := need(ctx.Corpse.Body.Hediffs, "the corpse pawn's hediffs")
	if err != nil {
		return 0, err
	}
	for _, h := range hediffs {
		if h.Def == hediffShamblerCorpse {
			return mul(val, p.GetMultiplier()), nil
		}
	}
	return val, nil
}

// partShamblerCrawling is StatPart_ShamblerCrawling: the factor applies to a
// spawned crawling shambler.
type partShamblerCrawling struct{ plain }

func (partShamblerCrawling) Class() string { return "StatPart_ShamblerCrawling" }

func (partShamblerCrawling) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_ShamblerCrawling](row, "StatPart_ShamblerCrawling")
	if err != nil {
		return 0, err
	}
	ctx := req.Subject.Context
	if ctx == nil {
		return val, nil
	}
	spawned, err := need(ctx.Spawned, "whether the thing is spawned")
	if err != nil || !spawned || ctx.Pawn == nil {
		return val, err
	}
	shambler, err := need(ctx.Pawn.Shambler, "whether the pawn is a shambler")
	if err != nil || !shambler {
		return val, err
	}
	crawling, err := need(ctx.Pawn.Body.Crawling, "whether the pawn is crawling")
	if err != nil || !crawling {
		return val, err
	}
	return mul(val, p.GetFactor()), nil
}

// partRevenantSpeed is StatPart_RevenantSpeed: for a visible Revenant, the
// speed curve over the seconds since it became visible.
type partRevenantSpeed struct{ plain }

func (partRevenantSpeed) Class() string { return "StatPart_RevenantSpeed" }

func (partRevenantSpeed) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	state, err := need(pawn.Body.Revenant, "the pawn's revenant state")
	if err != nil {
		return 0, err
	}
	if state.KindDef != kindRevenant || state.PsychologicallyInvisible {
		return val, nil
	}
	visible := max(state.LastBecameVisibleTick, state.LastForcedVisibleTick)
	if visible <= 0 {
		return val, nil
	}
	now, err := need(pawn.Body.TicksGame, "the current game tick")
	if err != nil {
		return 0, err
	}
	constants, err := req.Evaluator.catalog.GameConstants()
	if err != nil {
		return 0, err
	}
	seconds := float32(float32(now-visible) / 60) // GenTicks.TicksToSeconds
	factor, err := EvaluateCurve(constants.GetRevenantUtility().GetSpeedRangeFromBecameVisibleCurve(), seconds)
	if err != nil {
		return 0, err
	}
	return mul(val, factor), nil
}

// partTerrainMoveSpeed is StatPart_TerrainMoveSpeed: TransformValue does
// nothing (the part only explains the kind's terrain speed factors).
type partTerrainMoveSpeed struct{ plain }

func (partTerrainMoveSpeed) Class() string { return "StatPart_TerrainMoveSpeed" }

func (partTerrainMoveSpeed) Transform(_ *Request, _ proto.Message, val float32) (float32, error) {
	return val, nil
}

// --- ideology, sight, biotech ---

// preceptDef is the PreceptDef row an ideo's precept or role names.
func preceptDef(req *Request, name string) (*d.PreceptDef, error) {
	row := DefRow[*d.PreceptDef](req.Evaluator.catalog, name)
	if row == nil {
		return nil, fmt.Errorf("catalog has no precept def %s", name)
	}
	return row, nil
}

// pawnIdeo is the ideo of a thing request's pawn: nil when the part does not
// apply (no pawn, or a pawn without an ideo).
func pawnIdeo(req *Request) (*IdeoState, error) {
	pawn := req.pawn()
	if pawn == nil {
		return nil, nil
	}
	ideo, err := need(pawn.Body.Ideo, "the pawn's ideo")
	if err != nil || !ideo.Present {
		return nil, err
	}
	return &ideo, nil
}

// partBlindPsychicSensitivityOffset is StatPart_BlindPsychicSensitivityOffset:
// a pawn with every sight source missing and an ideo adds its precepts'
// blindPsychicSensitivityOffset.
type partBlindPsychicSensitivityOffset struct{ plain }

func (partBlindPsychicSensitivityOffset) Class() string {
	return "StatPart_BlindPsychicSensitivityOffset"
}

func (partBlindPsychicSensitivityOffset) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	blind, err := need(pawn.Body.SightSourcesAllMissing, "whether every sight source of the pawn is missing")
	if err != nil || !blind {
		return val, err
	}
	ideo, err := pawnIdeo(req)
	if err != nil || ideo == nil {
		return val, err
	}
	var offset float32
	for _, name := range ideo.Precepts {
		def, err := preceptDef(req, name)
		if err != nil {
			return 0, err
		}
		offset = float32(offset + def.GetBlindPsychicSensitivityOffset())
	}
	if approximately(offset, 0) {
		return val, nil
	}
	return float32(val + offset), nil
}

// partSightPsychicSensitivityOffset is StatPart_SightPsychicSensitivityOffset:
// a bonus that rises as the pawn's sight efficiency falls below startsAt.
type partSightPsychicSensitivityOffset struct{ plain }

func (partSightPsychicSensitivityOffset) Class() string {
	return "StatPart_SightPsychicSensitivityOffset"
}

func (partSightPsychicSensitivityOffset) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_SightPsychicSensitivityOffset](row, "StatPart_SightPsychicSensitivityOffset")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	efficiency, err := need(pawn.Body.SightEfficiency, "the pawn's sight tag efficiency")
	if err != nil || efficiency > p.GetStartsAt() {
		return val, err
	}
	offset := lerpDoubleClamped(p.GetStartsAt(), p.GetEndsAt(), p.GetMinBonus(), p.GetMaxBonus(), efficiency)
	if offset < 0.01 {
		return val, nil
	}
	return float32(val + offset), nil
}

// partOverseerStatOffset is StatPart_OverseerStatOffset: add the overseer's
// stat value (Biotech only).
type partOverseerStatOffset struct{ plain }

func (partOverseerStatOffset) Class() string { return "StatPart_OverseerStatOffset" }

func (partOverseerStatOffset) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_OverseerStatOffset](row, "StatPart_OverseerStatOffset")
	if err != nil {
		return 0, err
	}
	pawn, err := biotechPawn(req)
	if err != nil || pawn == nil {
		return val, err
	}
	overseer, err := need(pawn.Body.Overseer, "the pawn's overseer")
	if err != nil || !overseer.Present {
		return val, err
	}
	offset, ok := overseer.Stats[p.GetStat()]
	if !ok {
		return 0, fmt.Errorf("stat evaluation needs the overseer's %s stat: not observed", p.GetStat())
	}
	return float32(val + offset), nil
}

// partRoleConversionPower is StatPart_RoleConversionPower: the pawn's ideo
// role's convertPowerFactor.
type partRoleConversionPower struct{ plain }

func (partRoleConversionPower) Class() string { return "StatPart_RoleConversionPower" }

func (partRoleConversionPower) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ideo, err := pawnIdeo(req)
	if err != nil || ideo == nil || ideo.Role == "" {
		return val, err
	}
	role, err := preceptDef(req, ideo.Role)
	if err != nil {
		return 0, err
	}
	return mul(val, role.GetConvertPowerFactor()), nil
}

// partPlayerFactionLeader is StatPart_PlayerFactionLeader: add the offset to
// the player faction's leader. Only a pawn can be the leader.
type partPlayerFactionLeader struct{ plain }

func (partPlayerFactionLeader) Class() string { return "StatPart_PlayerFactionLeader" }

func (partPlayerFactionLeader) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_PlayerFactionLeader](row, "StatPart_PlayerFactionLeader")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	leader, err := need(pawn.Body.PlayerFactionLeader, "whether the pawn leads the player faction")
	if err != nil || !leader {
		return val, err
	}
	return float32(val + p.GetOffset()), nil
}

// partBedStat is StatPart_BedStat: multiply by the pawn's bed's stat.
type partBedStat struct{ plain }

func (partBedStat) Class() string { return "StatPart_BedStat" }

func (partBedStat) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_BedStat](row, "StatPart_BedStat")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	bed, err := need(pawn.Body.Bed, "the pawn's bed")
	if err != nil {
		return 0, err
	}
	switch bed.Kind {
	case BedNone:
		return val, nil
	case BedBuilt, BedCaravan:
		factor, ok := bed.Stats[p.GetStat()]
		if !ok {
			return 0, fmt.Errorf("stat evaluation needs the pawn's bed %s stat: not observed", p.GetStat())
		}
		return mul(val, factor), nil
	}
	return 0, fmt.Errorf("bed kind %q is not None, Bed or CaravanBed", bed.Kind)
}

// preceptProduct is the product of a per-precept factor over the ideo's
// precepts, for the Biotech cycle speed parts (they apply to any pawn with an
// ideo, Biotech active or not).
func preceptProduct(req *Request, factor func(*d.PreceptDef) float32) (float32, bool, error) {
	ideo, err := pawnIdeo(req)
	if err != nil || ideo == nil {
		return 0, false, err
	}
	product := float32(1)
	for _, name := range ideo.Precepts {
		def, err := preceptDef(req, name)
		if err != nil {
			return 0, false, err
		}
		product = mul(product, factor(def))
	}
	return product, true, nil
}

// partBiosculptingSpeedFactor is StatPart_BiosculptingSpeedFactor.
type partBiosculptingSpeedFactor struct{ plain }

func (partBiosculptingSpeedFactor) Class() string { return "StatPart_BiosculptingSpeedFactor" }

func (partBiosculptingSpeedFactor) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	factor, ok, err := preceptProduct(req, (*d.PreceptDef).GetBiosculpterPodCycleSpeedFactor)
	if err != nil || !ok {
		return val, err
	}
	return mul(val, factor), nil
}

// partGrowthVatSpeedFactor is StatPart_GrowthVatSpeedFactor.
type partGrowthVatSpeedFactor struct{ plain }

func (partGrowthVatSpeedFactor) Class() string { return "StatPart_GrowthVatSpeedFactor" }

func (partGrowthVatSpeedFactor) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	factor, ok, err := preceptProduct(req, (*d.PreceptDef).GetGrowthVatSpeedFactor)
	if err != nil || !ok {
		return val, err
	}
	return mul(val, factor), nil
}
