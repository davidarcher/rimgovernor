package simimport

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// hitLayers ports the part of a ranged hit's severity bookkeeping that the
// damage total sees: which body part a hit lands on and how many injuries of
// the full post-armor amount it adds.
//
// A hit with no chosen part (Projectile hits are such) lands on a part picked
// by HediffSet.GetRandomNotMissingPart: weight coverageAbs times the part
// def's hit chance factor for the damage def, over every part. When the damage
// def harms all layers until outside (DamageWorker_AddInjury.
// CheckDuplicateDamageToOuterParts) a hit on an inside part adds the same
// severity to each ancestor up to and including the first outside one that has
// coverage. The parts are all present and whole (A28).
//
// Part health, destruction, overkill and the per-part pain factor are ignored
// (A3, A33).
func (im *importer) hitLayers(body string) ([]combatsim.HitLayer, error) {
	def := bridge.DefRow[*d.BodyDef](im.cat, body)
	if def == nil || def.GetCorePart() == nil {
		return nil, unsupported("catalog has no body def %q", body)
	}
	byCopies := map[int]float64{}
	var total float64
	var walk func(part *d.BodyPartRecord, parentAbsWithChildren float64, ancestors []partCoverage) error
	walk = func(part *d.BodyPartRecord, parentAbsWithChildren float64, ancestors []partCoverage) error {
		partDef := bridge.DefRow[*d.BodyPartDef](im.cat, part.GetDef())
		if partDef == nil {
			return unsupported("catalog has no body part def %q", part.GetDef())
		}
		absWithChildren := 1.0
		if len(ancestors) > 0 {
			absWithChildren = parentAbsWithChildren * float64(part.GetCoverage())
		}
		own := absWithChildren
		for _, c := range part.GetParts() {
			own -= absWithChildren * float64(c.GetValue().GetCoverage())
		}
		weight := own * hitChanceFactor(partDef, im.dmg.def)
		if partDef.GetConceptual() {
			weight = 0
		}
		if weight > 0 {
			copies := 0
			if im.dmg.harmAllLayers && part.GetDepth() == d.BodyPartDepth_BODY_PART_DEPTH_INSIDE {
				for i := len(ancestors) - 1; i >= 0; i-- {
					if ancestors[i].own > 0 {
						copies++
					}
					if ancestors[i].depth == d.BodyPartDepth_BODY_PART_DEPTH_OUTSIDE {
						break
					}
				}
			}
			byCopies[copies] += weight
			total += weight
		}
		next := append(ancestors[:len(ancestors):len(ancestors)], partCoverage{own: own, depth: part.GetDepth()})
		for _, c := range part.GetParts() {
			if err := walk(c.GetValue(), absWithChildren, next); err != nil {
				return err
			}
		}
		return nil
	}
	if err := walk(def.GetCorePart(), 1, nil); err != nil {
		return nil, err
	}
	if !(total > 0) {
		return nil, unsupported("body def %q has no part a hit can land on", body)
	}
	var out []combatsim.HitLayer
	for copies := 0; copies < 16; copies++ {
		if w, ok := byCopies[copies]; ok {
			out = append(out, combatsim.HitLayer{Copies: copies, Chance: w / total})
		}
	}
	return out, nil
}

// partCoverage is an ancestor's exclusive coverage (BodyPartRecord.coverageAbs)
// and depth.
type partCoverage struct {
	own   float64
	depth d.BodyPartDepth
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
