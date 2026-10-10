package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MaintainTraining is the Military department's standing skill gap (#2619):
// colonists whose chosen combat skill is below its ladder's ceiling. It is a
// Standard whose target is no such colonist, and it is not gated by defense
// admission: the gap is its own deficit. Open, it asks layout for a training
// range (RoomDemand.Ranges) while a shooter is below, and for a sparring ring
// (RoomDemand.Rings) while two fighters are; a standing range or ring takes no
// further action from the Concern, because the native jobs spend the
// pawn-hours.
const MaintainTraining ConcernID = "MaintainTraining"

// trainingPriority ranks MaintainTraining with the other priority-3 chores.
const trainingPriority = 3

// TrainingReview counts the colony's colonists against the ceiling of each
// skill's own ladder: the combat skill level at which a colonist stops counting
// toward the gap. RimWorld skills run 0 to 20. Each pawn is judged on the one
// skill it trains (TrainingSkill) against that skill's ceiling.
type TrainingReview struct {
	// Capable are the adult colonists who can train a skill.
	Capable int
	// Below are the capable ones whose chosen skill is under its ceiling.
	Below int
	// ShootingBelow are the Below ones who train Shooting (the range's ceiling,
	// UnlockedTrainingTier); MeleeBelow those who train Melee (the ring's,
	// UnlockedSparringTier).
	ShootingBelow, MeleeBelow int
}

// TrainingSkill is the skill a pawn trains, mirroring the native choice: the
// higher usable level, then the stronger passion, then Shooting. A skill is
// unusable when disabled; Shooting also for a Brawler. False when neither is
// usable, or when either skill row is unread.
func TrainingSkill(p PawnProfile) (skill string, ok bool) {
	shooting, sRead := p.Skills["Shooting"]
	melee, mRead := p.Skills["Melee"]
	if !sRead || !mRead {
		return "", false
	}
	canShoot := !shooting.Disabled && !p.Effects.MeleeOnly
	canMelee := !melee.Disabled
	switch {
	case canShoot && canMelee:
		if melee.Level > shooting.Level || melee.Level == shooting.Level && gearPassionRank(melee.Passion) > gearPassionRank(shooting.Passion) {
			return "Melee", true
		}
		return "Shooting", true
	case canShoot:
		return "Shooting", true
	case canMelee:
		return "Melee", true
	}
	return "", false
}

// ReviewTraining counts the pawns below the ceiling of the skill each trains. A
// child is skipped, as is a pawn who can train neither skill. A pawn whose
// profile lacks a Melee or Shooting row is unread: it is no gap, and when no gap
// is found elsewhere the review is unknown rather than closed.
func ReviewTraining(profiles domain.Fact[[]PawnProfile], research domain.Fact[ResearchFacts]) domain.Fact[TrainingReview] {
	list, known := profiles.Value()
	if !known {
		return domain.Unknown[TrainingReview]()
	}
	shootingCeiling := UnlockedTrainingTier(research).Ceiling
	meleeCeiling := UnlockedSparringTier(research).Ceiling
	var r TrainingReview
	unread := false
	for _, p := range list {
		if p.Child {
			continue
		}
		skill, ok := TrainingSkill(p)
		if !ok {
			_, sRead := p.Skills["Shooting"]
			_, mRead := p.Skills["Melee"]
			unread = unread || !sRead || !mRead
			continue
		}
		r.Capable++
		level := p.Skills[skill].Level
		switch {
		case skill == "Shooting" && level < shootingCeiling:
			r.ShootingBelow++
		case skill == "Melee" && level < meleeCeiling:
			r.MeleeBelow++
		}
	}
	r.Below = r.ShootingBelow + r.MeleeBelow
	if r.Below == 0 && unread {
		return domain.Unknown[TrainingReview]()
	}
	return domain.Known(r)
}

// TrainingStands is the stands the training range should hold: none while no
// pawn that trains Shooting is below the range's ceiling, else RangeStandsFor
// the capable adults (every capable colonist, not only those under the target,
// so the count does not fall as colonists train). Unknown while the profiles or
// their skills are unread.
func TrainingStands(profiles domain.Fact[[]PawnProfile], research domain.Fact[ResearchFacts]) domain.Fact[int] {
	r, known := ReviewTraining(profiles, research).Value()
	if !known {
		return domain.Unknown[int]()
	}
	if r.ShootingBelow == 0 {
		return domain.Known(0)
	}
	return domain.Known(RangeStandsFor(r.Capable))
}

// TrainingRings is the markers the sparring ring should hold: none until at
// least two pawns that train Melee are below the ring's ceiling (a lone one has
// no partner), else RingMarkersFor the capable adults. Unknown while the
// profiles or their skills are unread.
func TrainingRings(profiles domain.Fact[[]PawnProfile], research domain.Fact[ResearchFacts]) domain.Fact[int] {
	r, known := ReviewTraining(profiles, research).Value()
	if !known {
		return domain.Unknown[int]()
	}
	if r.MeleeBelow < 2 {
		return domain.Known(0)
	}
	return domain.Known(RingMarkersFor(r.Capable))
}

// inspectTraining raises MaintainTraining while a capable colonist is below the
// target, its deficit the share of capable colonists that are.
func inspectTraining(c *roundsRun) error {
	review := ReviewTraining(c.f.WorkProfiles, c.f.Research)
	c.assess(MaintainTraining, trainingPriority, measured(review, func(r TrainingReview) bool { return r.Below == 0 }))
	if r, known := review.Value(); known && r.Below > 0 {
		c.raise(MaintainTraining, trainingPriority).Deficit = domain.Known(float64(r.Below) / float64(r.Capable))
	}
	return nil
}

// RangesWanted is the count the plan's training range (stands) or ring (markers)
// should hold: the count while the gap is open, else 0. A standing one stays and
// only grows; the plan never retires it.
func RangesWanted(stands domain.Fact[int]) int {
	if n, known := stands.Value(); known {
		return n
	}
	return 0
}
