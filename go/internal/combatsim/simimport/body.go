package simimport

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Body part tags and capacity defs the ported capacity workers read.
const (
	tagConsciousness   = "ConsciousnessSource"
	tagBloodPumping    = "BloodPumpingSource"
	tagBreathingSource = "BreathingSource"
	tagBreathingPath   = "BreathingPathway"
	tagBreathingCage   = "BreathingSourceCage"
	tagKidney          = "BloodFiltrationKidney"
	tagLiver           = "BloodFiltrationLiver"
	tagFiltration      = "BloodFiltrationSource"
	tagMetabolism      = "MetabolismSource"
	tagPelvis          = "Pelvis"
	tagSpine           = "Spine"
	tagMovingCore      = "MovingLimbCore"
	tagMovingSegment   = "MovingLimbSegment"
	tagMovingDigit     = "MovingLimbDigit"
	tagSight           = "SightSource"
	tagManipCore       = "ManipulationLimbCore"
	tagManipSegment    = "ManipulationLimbSegment"
	tagManipDigit      = "ManipulationLimbDigit"

	capMoving = "Moving"
	// missingPartHediff is the hediff a destroyed part leaves.
	missingPartHediff = "MissingBodyPart"
	// injuryHediffClass is the class of the hediffs a damage def adds.
	injuryHediffClass = "Hediff_Injury"
)

// lethalCapacities are the capacities with lethalFlesh the sim evaluates
// (Pawn_HealthTracker.ShouldBeDead's CapableOf loop), each with the worker the
// sim ports. Any other lethalFlesh capacity, or a different worker class, fails
// the import.
var lethalCapacities = map[string]string{
	"Consciousness": "PawnCapacityWorker_Consciousness", "BloodPumping": "PawnCapacityWorker_BloodPumping",
	"Breathing": "PawnCapacityWorker_Breathing", "BloodFiltration": "PawnCapacityWorker_BloodFiltration",
	"Metabolism": "PawnCapacityWorker_Metabolism",
}

// limbCapacities are the other capacities the downing and firing rules read.
var limbCapacities = map[string]string{"Moving": "PawnCapacityWorker_Moving", "Manipulation": "PawnCapacityWorker_Manipulation"}

// body derives the per-part injury model of a pawn kind's race from the catalog
// (A39): the body def's parts with their health, coverage and capacity tags, and
// the scalars from the pawn's stats and the damage def. It fails on anything the
// ported rules do not cover.
func (im *importer) body(race *d.RaceProperties, healthScale, shock, incoming float64, ignoresPainShock bool) (*combatsim.Body, error) {
	bodyName := race.GetBody()
	def := bridge.DefRow[*d.BodyDef](im.cat, bodyName)
	if def == nil || def.GetCorePart() == nil {
		return nil, unsupported("catalog has no body def %q", bodyName)
	}
	if ignoresPainShock {
		return nil, unsupported("a pawn kind that ignores pain shock is not modeled")
	}
	if err := im.checkCapacities(); err != nil {
		return nil, err
	}
	pps, missingPPS, err := im.painRates()
	if err != nil {
		return nil, err
	}
	bleed, missingBleed, merge := im.bleedRates()
	// Pawn_HealthTracker.CanBleed: a blood def, a flesh race, a positive factor.
	canBleed := race.GetBloodDef() != "" && race.GetBleedRateFactor() > 0
	fleshName := race.GetFleshType()
	if fleshName == "" {
		fleshName = "Normal" // RaceProperties.FleshType: a race with none is Normal
	}
	if flesh := bridge.DefRow[*d.FleshTypeDef](im.cat, fleshName); flesh == nil || !flesh.GetIsOrganic() {
		canBleed = false
	}
	b := &combatsim.Body{
		HealthScale: healthScale, PainPerSeverity: pps, MissingPainPerSeverity: missingPPS, PainShock: shock,
		IncomingDamageFactor: incoming, HarmAllLayers: im.dmg.harmAllLayers,
		OverkillLow: im.dmg.overkillLow, OverkillHigh: im.dmg.overkillHigh,
		BleedPerSeverity: bleed, MissingBleedPerSeverity: missingBleed, MergeInjuries: merge,
	}
	if canBleed {
		b.BleedFactor = float64(race.GetBleedRateFactor())
	}
	constants, err := im.cat.GameConstants()
	if err != nil {
		return nil, unsupported("game constants: %v", err)
	}
	b.LethalDamage = float64(constants.GetHealthTuning().GetBaseTotalDamageLethalThreshold()) * healthScale
	moving := bridge.DefRow[*d.PawnCapacityDef](im.cat, capMoving)
	b.MovingMinForCapable = float64(moving.GetMinForCapable())

	var tags [][]string
	var walk func(part *d.BodyPartRecord, parent int, parentAbs float64) error
	walk = func(part *d.BodyPartRecord, parent int, parentAbs float64) error {
		partDef := bridge.DefRow[*d.BodyPartDef](im.cat, part.GetDef())
		if partDef == nil {
			return unsupported("catalog has no body part def %q", part.GetDef())
		}
		var inside bool
		switch part.GetDepth() {
		case d.BodyPartDepth_BODY_PART_DEPTH_INSIDE:
			inside = true
		case d.BodyPartDepth_BODY_PART_DEPTH_OUTSIDE:
		default:
			return unsupported("body part %q has no depth", part.GetDef())
		}
		// BodyDef.ResolveReferences: coverageAbs is the part's share of the body
		// less its children's.
		abs := 1.0
		if parent >= 0 {
			abs = parentAbs * float64(part.GetCoverage())
		}
		own := abs
		for _, c := range part.GetParts() {
			own -= abs * float64(c.GetValue().GetCoverage())
		}
		weight := own * hitChanceFactor(partDef, im.dmg.def)
		if partDef.GetConceptual() || !(weight > 0) {
			weight = 0
		}
		maxHealth := math.Ceil(float64(float32(partDef.GetHitPoints()) * float32(healthScale))) // BodyPartDef.GetMaxHealth
		idx := len(b.Parts)
		b.Parts = append(b.Parts, combatsim.BodyPart{
			Parent: parent, Inside: inside, Coverage: own, HitWeight: weight, MaxHealth: maxHealth,
			Solid: partDef.GetSolid(), Destroyable: partDef.GetDestroyableByDamage(),
			HitPoints: float64(partDef.GetHitPoints()), BleedRate: float64(partDef.GetBleedRate()),
			Tagged: len(partDef.GetTags()) > 0, HasChildren: len(part.GetParts()) > 0,
		})
		tags = append(tags, partDef.GetTags())
		for _, c := range part.GetParts() {
			if err := walk(c.GetValue(), idx, abs); err != nil {
				return err
			}
		}
		return nil
	}
	if err := walk(def.GetCorePart(), -1, 1); err != nil {
		return nil, err
	}

	withTag := func(tag string) []int {
		var out []int
		for i, ts := range tags {
			if slices.Contains(ts, tag) {
				out = append(out, i)
			}
		}
		return out
	}
	b.Consciousness, b.BloodPumping = withTag(tagConsciousness), withTag(tagBloodPumping)
	b.BreathingSource, b.BreathingPathway, b.BreathingCage = withTag(tagBreathingSource), withTag(tagBreathingPath), withTag(tagBreathingCage)
	b.FiltrationKidney, b.FiltrationLiver, b.FiltrationSource = withTag(tagKidney), withTag(tagLiver), withTag(tagFiltration)
	b.Metabolism, b.Pelvis, b.Spine = withTag(tagMetabolism), withTag(tagPelvis), withTag(tagSpine)
	b.Sight = withTag(tagSight)

	// BodyPartRecord.GetChildParts(tag): the part itself if tagged, then its
	// descendants in order. GetConnectedParts(tag) climbs through tagged parents
	// first.
	has := func(i int, tag string) bool { return slices.Contains(tags[i], tag) }
	childParts := func(root int, tag string) []int {
		var out []int
		for i := root; i < len(b.Parts); i++ {
			if (i == root || inSubtree(b, i, root)) && has(i, tag) {
				out = append(out, i)
			}
		}
		return out
	}
	limbs := func(core, segment, digit string) []combatsim.Limb {
		var out []combatsim.Limb
		for _, c := range withTag(core) {
			top := c
			for p := b.Parts[top].Parent; p >= 0 && has(p, segment); p = b.Parts[top].Parent {
				top = p
			}
			out = append(out, combatsim.Limb{Core: c, Segments: childParts(top, segment), Digits: childParts(c, digit)})
		}
		return out
	}
	b.Moving = limbs(tagMovingCore, tagMovingSegment, tagMovingDigit)
	b.Manipulation = limbs(tagManipCore, tagManipSegment, tagManipDigit)
	if len(b.Moving) == 0 || len(b.Manipulation) == 0 {
		return nil, unsupported("body def %q has no moving or manipulating limb", bodyName)
	}
	return b, nil
}

// inSubtree reports whether part i lies under root.
func inSubtree(b *combatsim.Body, i, root int) bool {
	for p := b.Parts[i].Parent; p >= 0; p = b.Parts[p].Parent {
		if p == root {
			return true
		}
	}
	return false
}

// checkCapacities fails unless every capacity the sim evaluates is the one the
// sim ports and no other capacity is lethal to flesh.
func (im *importer) checkCapacities() error {
	for name := range im.cat.Rows((&d.PawnCapacityDef{}).ProtoReflect().Descriptor().FullName()) {
		c := bridge.DefRow[*d.PawnCapacityDef](im.cat, name)
		worker, ported := lethalCapacities[name]
		limb, isLimb := limbCapacities[name]
		switch {
		case c == nil:
			return unsupported("catalog has no pawn capacity %q", name)
		case ported || isLimb:
			want := worker + limb
			if cls := c.GetWorkerClass(); cls != want && !strings.HasSuffix(cls, "."+want) {
				return unsupported("pawn capacity %q worker %q is not the ported %q", name, c.GetWorkerClass(), want)
			}
			if ported && (!c.GetLethalFlesh() || c.GetMinForCapable() != 0) {
				return unsupported("pawn capacity %q is not lethal below 0", name)
			}
		case c.GetLethalFlesh():
			return unsupported("pawn capacity %q is lethal to flesh and not ported", name)
		}
	}
	return nil
}

// painRates reads the pain per severity of the damage's injury hediff and of
// the missing-part hediff, and fails when the injury hediff has anything the
// per-part model leaves out (stages, lethal severity, painless chance).
func (im *importer) painRates() (injury, missing float64, err error) {
	h := bridge.DefRow[*d.HediffDef](im.cat, im.dmg.hediff)
	switch {
	case h == nil || h.GetInjuryProps() == nil:
		return 0, 0, unsupported("catalog has no injury hediff %q", im.dmg.hediff)
	case h.GetHediffClass() != injuryHediffClass && !strings.HasSuffix(h.GetHediffClass(), "."+injuryHediffClass):
		return 0, 0, unsupported("hediff %q class %q is not %s", im.dmg.hediff, h.GetHediffClass(), injuryHediffClass)
	case len(h.GetStages()) > 0, h.GetLethalSeverity() > 0, h.GetChanceToCauseNoPain() > 0:
		return 0, 0, unsupported("hediff %q has stages, a lethal severity or a painless chance, which the per-part model leaves out", im.dmg.hediff)
	}
	m := bridge.DefRow[*d.HediffDef](im.cat, missingPartHediff)
	if m == nil || m.GetInjuryProps() == nil {
		return 0, 0, unsupported("catalog has no hediff %q", missingPartHediff)
	}
	return float64(h.GetInjuryProps().GetPainPerSeverity()), float64(m.GetInjuryProps().GetPainPerSeverity()), nil
}

// bleedRates reads the bleed rate per severity of the damage's injury hediff and
// of the missing-part hediff, and whether the injury hediff merges
// (injuryProps.canMerge). painRates has already checked both hediffs exist.
func (im *importer) bleedRates() (injury, missing float64, merge bool) {
	h := bridge.DefRow[*d.HediffDef](im.cat, im.dmg.hediff)
	m := bridge.DefRow[*d.HediffDef](im.cat, missingPartHediff)
	return float64(h.GetInjuryProps().GetBleedRate()), float64(m.GetInjuryProps().GetBleedRate()), h.GetInjuryProps().GetCanMerge()
}

// hitChanceFactor is BodyPartDef.GetHitChanceFactorFor.
func hitChanceFactor(part *d.BodyPartDef, damageDef string) float64 {
	for _, e := range part.GetHitChanceFactors() {
		if e.GetKey() == damageDef {
			return float64(e.GetValue())
		}
	}
	return 1
}
