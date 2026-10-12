package combatsim

import "math"

// Body is the per-part injury model of one pawn kind: the body def's parts with
// the health, coverage and capacity roles the ported rules read, and the
// scalars those rules take from the pawn and the damage def. The importer
// derives it from the catalog (simimport/body.go); every rule here is a port of
// the pinned game's code, named on the method, and approximations.md (A1, A33,
// A34, A39) lists what is left out.
type Body struct {
	// Parts is the body tree in pre-order, Parts[0] the core part, every
	// parent before its children.
	Parts []BodyPart
	// HealthScale is Pawn.HealthScale.
	HealthScale float64
	// PainPerSeverity is the injury hediff's injuryProps.painPerSeverity and
	// MissingPainPerSeverity the MissingBodyPart hediff's.
	PainPerSeverity, MissingPainPerSeverity float64
	// PainShock is the PainShockThreshold stat; Verse.Pawn_HealthTracker
	// .InPainShock downs a pawn whose total pain reaches it.
	PainShock float64
	// LethalDamage is Pawn_HealthTracker.LethalDamageThreshold, 150 x
	// HealthScale.
	LethalDamage float64
	// IncomingDamageFactor is the IncomingDamageFactor stat applied to violent
	// damage after armor.
	IncomingDamageFactor float64
	// HarmAllLayers is the damage def's harmAllLayersUntilOutside; OverkillLow
	// and OverkillHigh its overkillPctToDestroyPart range.
	HarmAllLayers             bool
	OverkillLow, OverkillHigh float64
	// MovingMinForCapable is the Moving capacity's minForCapable.
	MovingMinForCapable float64
	// BleedPerSeverity is the injury hediff's injuryProps.bleedRate and
	// MissingBleedPerSeverity the MissingBodyPart hediff's; BleedFactor is the
	// race's bleedRateFactor, 0 for a pawn that cannot bleed (no blood def or
	// not flesh). MergeInjuries is the injury hediff's injuryProps.canMerge.
	// They feed the summary-health and bleed-rate readings of the serious
	// injury stop (A22); a zero value reads no bleeding.
	BleedPerSeverity, MissingBleedPerSeverity, BleedFactor float64
	MergeInjuries                                          bool

	// Tag lists the capacity workers read: the parts carrying each body part
	// tag. Empty leaves the capacity part efficiency at 1 (CalculateTagEfficiency).
	Consciousness, BloodPumping, BreathingSource, BreathingPathway, BreathingCage []int
	FiltrationKidney, FiltrationLiver, FiltrationSource                           []int
	Metabolism, Pelvis, Spine                                                     []int
	// Moving and Manipulation are the limbs the two limb capacities average.
	Moving, Manipulation []Limb
}

// BodyPart is one BodyPartRecord with its BodyPartDef values.
type BodyPart struct {
	// Parent indexes the parent part, -1 for the core part.
	Parent int
	// Inside is depth == Inside (otherwise Outside).
	Inside bool
	// Coverage is BodyPartRecord.coverageAbs.
	Coverage float64
	// HitWeight is Coverage x BodyPartDef.GetHitChanceFactorFor(the damage
	// def): the weight HediffSet.GetRandomNotMissingPart gives the part.
	HitWeight float64
	// MaxHealth is BodyPartDef.GetMaxHealth: ceil(hitPoints x HealthScale).
	MaxHealth float64
	// Solid is BodyPartDef.IsSolid, Destroyable destroyableByDamage.
	Solid, Destroyable bool
	// HitPoints is the def's hitPoints before the HealthScale; BleedRate its
	// bleedRate; Tagged whether it has any body part tag and HasChildren any
	// child part (the missing-part summary-health rule reads both).
	HitPoints, BleedRate float64
	Tagged, HasChildren  bool
}

// Limb is one limb core of a limb capacity with the parts the capacity
// multiplies in: BodyPartRecord.GetConnectedParts(segment tag) and
// GetChildParts(digit tag).
type Limb struct {
	Core             int
	Segments, Digits []int
}

// injuryState is the hediffs of one pawn that the rules read: the summed
// severity of the injuries on each part and which parts are missing.
type injuryState struct {
	// sev is the summed severity of the injuries on a part.
	sev     []float64
	missing []bool
	// fresh marks a part destroyed by the damage itself (Hediff_MissingPart
	// .IsFresh), whose loss hurts while its parent is whole.
	fresh []bool
	// total is the severity of every injury (Hediff_Injury).
	total float64
	// keep is the product of 1 - min(severity / (75 x HealthScale), 0.95) over
	// the hits on a part that stay separate hediffs (summaryHealth).
	keep []float64
	// blood is the BloodLoss hediff's severity (0 while the pawn has none).
	blood float64
}

// The BloodLoss hediff (Core Hediffs_Global_Misc.xml) and the giver that grows
// it (Verse.HediffGiver_Bleeding, run by Pawn_HealthTracker.HealthTickInterval
// when pawn.IsHashIntervalTick(60)): severity rises by bleed rate x 0.001 an
// interval while the rate is at least 0.1 and falls by 1/3000 otherwise; it is
// lethal at 1. Its stages (minSeverity 0, 0.15, 0.30, 0.45, 0.60) offset the
// Consciousness capacity by 0, -0.1, -0.2, -0.4, -0.4 and the last also caps it
// at 0.1.
const (
	bloodLossInterval   = 60
	bloodLossPerBleed   = 0.001
	bloodLossRecovery   = 0.00033333333
	bloodLossMinBleed   = 0.1
	bloodLossLethal     = 1.0
	bloodLossStageCount = 5
)

var (
	bloodStageMin    = [bloodLossStageCount]float64{0, 0.15, 0.30, 0.45, 0.60}
	bloodStageOffset = [bloodLossStageCount]float64{0, -0.1, -0.2, -0.4, -0.4}
)

// bloodStage is the index of the BloodLoss stage a severity falls in
// (Hediff.CurStageIndex: the last stage whose minSeverity it reaches).
func bloodStage(sev float64) int {
	i := 0
	for j, m := range bloodStageMin {
		if sev >= m {
			i = j
		}
	}
	return i
}

// bloodTick ports HediffGiver_Bleeding.OnIntervalPassed for one interval and
// reports whether the hediff changed stage or became lethal, the cases in
// which Hediff.Severity notifies the health tracker (CheckForStateChange).
func (b *Body) bloodTick(st *injuryState) (changed bool) {
	before := bloodStage(st.blood)
	if rate := b.bleedRate(st); rate >= bloodLossMinBleed {
		st.blood += rate * bloodLossPerBleed
	} else {
		st.blood = math.Max(st.blood-bloodLossRecovery, 0)
	}
	if st.blood >= bloodLossLethal {
		st.blood = bloodLossLethal
		return true
	}
	return bloodStage(st.blood) != before
}

func newInjuryState(n int) injuryState {
	st := injuryState{sev: make([]float64, n), missing: make([]bool, n), fresh: make([]bool, n), keep: make([]float64, n)}
	for i := range st.keep {
		st.keep[i] = 1
	}
	return st
}

// validate reports why the body cannot be simulated, or "".
func (b *Body) validate() string {
	n := len(b.Parts)
	if n == 0 {
		return "has no parts"
	}
	for i, p := range b.Parts {
		switch {
		case i == 0 && p.Parent != -1, i > 0 && (p.Parent < 0 || p.Parent >= i):
			return "has a part whose parent does not come before it"
		case !(p.MaxHealth >= 1), p.Coverage < 0, p.HitWeight < 0:
			return "has a part with no health or a negative coverage or hit weight"
		}
	}
	if !(b.HealthScale > 0) || !(b.PainPerSeverity > 0) || b.MissingPainPerSeverity < 0 || !(b.PainShock > 0) ||
		!(b.LethalDamage > 0) || !(b.IncomingDamageFactor >= 0) || b.OverkillLow < 0 || b.OverkillHigh < b.OverkillLow {
		return "has an invalid scalar (health scale, pain, shock or lethal threshold)"
	}
	hittable := 0.0
	for _, p := range b.Parts {
		hittable += p.HitWeight
	}
	if !(hittable > 0) {
		return "has no part a hit can land on"
	}
	inRange := func(idx []int) bool {
		for _, i := range idx {
			if i < 0 || i >= n {
				return false
			}
		}
		return true
	}
	for _, l := range append(append([]Limb(nil), b.Moving...), b.Manipulation...) {
		if !inRange([]int{l.Core}) || !inRange(l.Segments) || !inRange(l.Digits) {
			return "has a limb naming a part that is not in the body"
		}
	}
	for _, idx := range [][]int{b.Consciousness, b.BloodPumping, b.BreathingSource, b.BreathingPathway, b.BreathingCage,
		b.FiltrationKidney, b.FiltrationLiver, b.FiltrationSource, b.Metabolism, b.Pelvis, b.Spine} {
		if !inRange(idx) {
			return "has a capacity tag naming a part that is not in the body"
		}
	}
	return ""
}

// partHealth ports HediffSet.GetPartHealth for a part with no added parts: its
// maximum less its injuries, rounded, never below 1 when the part cannot be
// destroyed by damage, 0 once missing.
func (b *Body) partHealth(st *injuryState, i int) float64 {
	if st.missing[i] {
		return 0
	}
	p := &b.Parts[i]
	h := math.Max(p.MaxHealth-st.sev[i], 0)
	if !p.Destroyable {
		h = math.Max(h, 1)
	}
	return math.RoundToEven(h)
}

// preservesOutside is DamageWorker_AddInjury.ShouldReduceDamageToPreservePart:
// an outside part that is not the core.
func (b *Body) preservesOutside(i int) bool { return i != 0 && !b.Parts[i].Inside }

// partEfficiency ports PawnCapacityUtility.CalculatePartEfficiency for a part
// with no added part and no stage that moves efficiency (the importer refuses
// an injury hediff with stages).
func (b *Body) partEfficiency(st *injuryState, i int) float64 {
	if par := b.Parts[i].Parent; par >= 0 && st.missing[par] {
		return 0
	}
	frac := b.partHealth(st, i) / b.Parts[i].MaxHealth
	if frac == 1 {
		return 1
	}
	if b.preservesOutside(i) {
		frac = clamp01((frac - 0.1) / 0.9)
	}
	return math.Max(frac, 0)
}

func clamp01(f float64) float64 { return math.Min(math.Max(f, 0), 1) }

// tagEfficiency ports PawnCapacityUtility.CalculateTagEfficiency with no lerp
// range: the mean efficiency of the parts with the tag, capped at maximum, 1
// for a body with no such part.
func (b *Body) tagEfficiency(st *injuryState, parts []int, maximum float64) float64 {
	if len(parts) == 0 {
		return 1
	}
	sum := 0.0
	for _, i := range parts {
		sum += b.partEfficiency(st, i)
	}
	return math.Min(sum/float64(len(parts)), maximum)
}

// limbEfficiency ports PawnCapacityUtility.CalculateLimbEfficiency: the mean
// over limb cores of the core's efficiency times its connected segments', with
// the digits' mean efficiency lerped in by the appendage weight. It returns the
// share of limbs still working.
func (b *Body) limbEfficiency(st *injuryState, limbs []Limb, appendageWeight float64) (level, functional float64) {
	if len(limbs) == 0 {
		return 0, 0
	}
	sum, working := 0.0, 0
	for _, l := range limbs {
		e := b.partEfficiency(st, l.Core)
		for _, s := range l.Segments {
			e *= b.partEfficiency(st, s)
		}
		if len(l.Digits) > 0 {
			avg := 0.0
			for _, d := range l.Digits {
				avg += b.partEfficiency(st, d)
			}
			avg /= float64(len(l.Digits))
			e = e + (e*avg-e)*appendageWeight // Mathf.Lerp(e, e*avg, weight)
		}
		sum += e
		if e > 0 {
			working++
		}
	}
	return sum / float64(len(limbs)), float64(working) / float64(len(limbs))
}

// pain ports HediffSet.CalculatePain for a pawn with only injuries and lost
// parts: each injury's severity x painPerSeverity, a freshly lost outside,
// non-solid part's maximum health x the missing part's painPerSeverity (a lost
// part whose parent is also lost hurts only through the parent), over
// HealthScale, clamped to [0, 1].
func (b *Body) pain(st *injuryState) float64 {
	total := st.total * b.PainPerSeverity
	for i := range b.Parts {
		p := &b.Parts[i]
		if st.fresh[i] && !p.Inside && !p.Solid && (p.Parent < 0 || !st.missing[p.Parent]) {
			total += p.MaxHealth * b.MissingPainPerSeverity
		}
	}
	return clamp01(total / b.HealthScale)
}

// capacities are the levels the downing and firing rules read.
type capacities struct {
	consciousness, moving, manipulation float64
	// lethalLost is set when any capacity with lethalFlesh is no longer
	// capable (level at or below its minForCapable of 0).
	lethalLost bool
}

// roundedHundredth is GenMath.RoundedHundredth.
func roundedHundredth(f float64) float64 { return math.RoundToEven(f*100) / 100 }

// levels ports PawnCapacityUtility.CalculateCapacityLevel for the capacities
// Pawn_HealthTracker reads (the four vital ones, Metabolism, Consciousness,
// Moving and Manipulation) through their workers, with the capacity defs'
// zeroIfCannotBeAwake. Hediffs carry no capacity modifiers here (A39).
func (b *Body) levels(st *injuryState) capacities {
	pumping := roundedHundredth(b.tagEfficiency(st, b.BloodPumping, math.MaxFloat64))
	breathing := roundedHundredth(b.tagEfficiency(st, b.BreathingSource, math.MaxFloat64) *
		b.tagEfficiency(st, b.BreathingPathway, 1) * b.tagEfficiency(st, b.BreathingCage, 1))
	var filtration float64
	if len(b.FiltrationKidney) > 0 {
		filtration = b.tagEfficiency(st, b.FiltrationKidney, math.MaxFloat64) * b.tagEfficiency(st, b.FiltrationLiver, math.MaxFloat64)
	} else {
		filtration = b.tagEfficiency(st, b.FiltrationSource, math.MaxFloat64)
	}
	filtration = roundedHundredth(filtration)
	metabolism := roundedHundredth(b.tagEfficiency(st, b.Metabolism, math.MaxFloat64))

	// PawnCapacityWorker_Consciousness.
	c := b.tagEfficiency(st, b.Consciousness, math.MaxFloat64)
	if off := math.Min(math.Max(0+(b.pain(st)-0.1)/(1-0.1)*0.4, 0), 0.4); off >= 0.01 {
		c -= off
	}
	lerp := func(a, z, t float64) float64 { return a + (z-a)*clamp01(t) }
	c = lerp(c, c*math.Min(pumping, 1), 0.2)
	c = lerp(c, c*math.Min(breathing, 1), 0.2)
	c = lerp(c, c*math.Min(filtration, 1), 0.1)
	if c > 0 { // PawnCapacityUtility.CalculateCapacityLevel: the hediff capMods, BloodLoss's only
		stage := bloodStage(st.blood)
		c += bloodStageOffset[stage]
		if stage == bloodLossStageCount-1 {
			c = math.Min(c, 0.1)
		}
	}
	c = roundedHundredth(math.Max(c, 0))

	out := capacities{consciousness: c}
	out.lethalLost = !(c > 0) || !(pumping > 0) || !(breathing > 0) || !(filtration > 0) || !(metabolism > 0)
	if !(c >= 0.3) { // PawnCapacitiesHandler.CanBeAwake: both limb capacities are zeroIfCannotBeAwake
		return out
	}

	// PawnCapacityWorker_Moving.
	if lvl, functional := b.limbEfficiency(st, b.Moving, 0.4); functional >= 0.4999 {
		m := lvl * b.tagEfficiency(st, b.Pelvis, math.MaxFloat64) * b.tagEfficiency(st, b.Spine, math.MaxFloat64)
		m = lerp(m, m*breathing, 0.2)
		m = lerp(m, m*pumping, 0.2)
		out.moving = roundedHundredth(math.Max(m*math.Min(c, 1), 0))
	}
	// PawnCapacityWorker_Manipulation.
	lvl, _ := b.limbEfficiency(st, b.Manipulation, 0.8)
	out.manipulation = roundedHundredth(math.Max(lvl*c, 0))
	return out
}

// shouldBeDead ports Pawn_HealthTracker.ShouldBeDead for the hediffs held: a
// lethal capacity lost, the core part's efficiency gone, or injuries past the
// lethal damage threshold.
func (b *Body) shouldBeDead(st *injuryState, caps capacities) bool {
	return caps.lethalLost || b.partEfficiency(st, 0) <= 0.0001 || st.total >= b.LethalDamage || st.blood >= bloodLossLethal
}

// shouldBeDowned ports Pawn_HealthTracker.ShouldBeDowned for a pawn that is
// not always downed: pain shock, no consciousness to stay awake, or a Moving
// capacity at or below its minForCapable.
func (b *Body) shouldBeDowned(st *injuryState, caps capacities) bool {
	return b.pain(st) >= b.PainShock || !(caps.consciousness >= 0.3) || !(caps.moving > b.MovingMinForCapable)
}

// chooseHitPart ports HediffSet.GetRandomNotMissingPart for a hit with no
// chosen height or depth: a part drawn with weight HitWeight among the parts
// that are not missing, u in [0, 1). -1 when none can be hit.
func (b *Body) chooseHitPart(st *injuryState, u float64) int {
	total := 0.0
	for i := range b.Parts {
		if !st.missing[i] {
			total += b.Parts[i].HitWeight
		}
	}
	if !(total > 0) {
		return -1
	}
	x, last := u*total, -1
	for i := range b.Parts {
		w := b.Parts[i].HitWeight
		if st.missing[i] || !(w > 0) {
			continue
		}
		if x < w {
			return i
		}
		x -= w
		last = i
	}
	return last
}

// addInjury adds one injury of the given severity to a part that is not
// missing (HediffSet.AddDirect) and returns the damage it dealt, the severity
// capped at the part's health before it (DamageWorker.FinalizeAndAddInjury's
// totalDamageDealt). A part past its health that is not the core is destroyed:
// it and the parts under it go missing and lose their injuries
// (Hediff_MissingPart.PostAdd, Pawn_HealthTracker.RestorePart).
func (b *Body) addInjury(st *injuryState, part int, severity float64) float64 {
	dealt := math.Min(severity, b.partHealth(st, part))
	st.sev[part] += severity
	st.total += severity
	st.keep[part] *= 1 - math.Min(severity/(75*b.HealthScale), 0.95)
	if part != 0 && b.partHealth(st, part) == 0 {
		st.fresh[part] = true
		b.lose(st, part)
	}
	return dealt
}

// lose marks the part and every part beneath it missing and drops their
// injuries. Parts are in pre-order, so a part's subtree follows it.
func (b *Body) lose(st *injuryState, part int) {
	st.missing[part] = true
	st.total -= st.sev[part]
	st.sev[part], st.keep[part] = 0, 1
	for i := part + 1; i < len(b.Parts); i++ {
		if !b.inSubtree(i, part) {
			continue
		}
		st.total -= st.sev[i]
		st.sev[i], st.keep[i] = 0, 1
		st.missing[i], st.fresh[i] = true, false
	}
}

func (b *Body) inSubtree(i, root int) bool {
	for p := b.Parts[i].Parent; p >= 0; p = b.Parts[p].Parent {
		if p == root {
			return true
		}
	}
	return false
}

// overkillChance is FloatRange.InverseLerpThroughRange over the damage def's
// overkillPctToDestroyPart: the chance an outside part is destroyed rather than
// left at 1 health when a hit exceeds the health it has.
func (b *Body) overkillChance(f float64) float64 {
	if b.OverkillHigh == b.OverkillLow {
		return 0 // Mathf.InverseLerp(a, a, f)
	}
	return clamp01((f - b.OverkillLow) / (b.OverkillHigh - b.OverkillLow))
}

// summaryHealth ports SummaryHealthHandler.SummaryHealthPercent: the product,
// over the injury hediffs, of 1 - min(severity / (75 x HealthScale), 0.95), and
// the same for each missing part that is a fresh non-solid outside extremity
// (Hediff_MissingPart.SummaryHealthPercentImpact: its def hit points over the
// same scale, when it has tags, child parts or bleeds), clamped to [0.05, 1].
// The injuries of a part whose hediff can merge are one hediff of the summed
// severity (Hediff_Injury.TryMergeWith), otherwise each hit is its own.
func (b *Body) summaryHealth(st *injuryState) float64 {
	scale := 75 * b.HealthScale
	out := 1.0
	for i := range b.Parts {
		p := &b.Parts[i]
		switch {
		case st.missing[i]:
			if st.fresh[i] && !p.Inside && !p.Solid && (p.Tagged || p.HasChildren || b.partBleed(i) > 0) {
				out *= 1 - math.Min(p.HitPoints/scale, 0.95)
			}
		case b.MergeInjuries:
			out *= 1 - math.Min(st.sev[i]/scale, 0.95)
		default:
			out *= st.keep[i]
		}
	}
	return math.Min(math.Max(out, 0.05), 1)
}

// partBleed is the bleed rate (before the division by HealthScale) of a
// destroyed part's Hediff_MissingPart, when it counts: the part's maximum
// health x the missing-part hediff's bleedRate x the part's bleedRate x the
// race factor.
func (b *Body) partBleed(i int) float64 {
	p := &b.Parts[i]
	return p.MaxHealth * b.MissingBleedPerSeverity * p.BleedRate * b.BleedFactor
}

// bleedRate ports HediffSet.CalculateBleedRate for the hediffs held: each
// injury on a non-solid part bleeds severity x the injury hediff's bleedRate x
// the part's bleedRate x the race factor (Hediff_Injury.BleedRate; nothing is
// tended or permanent here), a fresh missing outside non-solid part whose parent
// is whole bleeds partBleed, all over HealthScale. 0 for a race that cannot
// bleed (Pawn_HealthTracker.CanBleed).
func (b *Body) bleedRate(st *injuryState) float64 {
	if !(b.BleedFactor > 0) {
		return 0
	}
	total := 0.0
	for i := range b.Parts {
		p := &b.Parts[i]
		switch {
		case st.missing[i]:
			if st.fresh[i] && !p.Inside && !p.Solid && (p.Parent < 0 || !st.missing[p.Parent]) {
				total += b.partBleed(i)
			}
		case !p.Solid:
			total += st.sev[i] * b.BleedPerSeverity * p.BleedRate * b.BleedFactor
		}
	}
	return total / b.HealthScale
}
