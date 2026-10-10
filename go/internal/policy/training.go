package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MaintainTraining is the Military department's standing skill gap (#2619):
// shooters whose Shooting is below the unlocked tier's ceiling. It is a Standard whose target is no such colonist, and it
// is not gated by defense admission: the gap is its own deficit. Open, it asks
// layout for a training range (RoomDemand.Ranges); a standing range takes no
// further action from the Concern, because the native training job (#2610)
// spends the pawn-hours.
const MaintainTraining ConcernID = "MaintainTraining"

// trainingPriority ranks MaintainTraining with the other priority-3 chores.
const trainingPriority = 3

// TrainingReview counts the colony's colonists against the ceiling of the best
// unlocked tier (UnlockedTrainingTier): the combat skill level at which a
// colonist stops counting toward the gap. RimWorld skills run 0 to 20.
type TrainingReview struct {
	// Capable are the adult colonists who can shoot.
	Capable int
	// Below are the capable ones whose Shooting is under the ceiling.
	Below int
}

// ReviewTraining counts the shooters below the ceiling. A child is skipped, as
// is a pawn who cannot shoot (Shooting disabled, or a Brawler): the range trains
// shooting only. A pawn whose profile carries no Shooting row is unread: it
// is no gap, and when no gap is found elsewhere the review is unknown rather
// than closed.
func ReviewTraining(profiles domain.Fact[[]PawnProfile], research domain.Fact[ResearchFacts]) domain.Fact[TrainingReview] {
	list, known := profiles.Value()
	if !known {
		return domain.Unknown[TrainingReview]()
	}
	ceiling := UnlockedTrainingTier(research).Ceiling
	var r TrainingReview
	unread := false
	for _, p := range list {
		if p.Child {
			continue
		}
		s, read := p.Skills["Shooting"]
		if !read {
			unread = true
			continue
		}
		if s.Disabled || p.Effects.MeleeOnly {
			continue
		}
		r.Capable++
		if s.Level < ceiling {
			r.Below++
		}
	}
	if r.Below == 0 && unread {
		return domain.Unknown[TrainingReview]()
	}
	return domain.Known(r)
}

// TrainingStands is the stands the training range should hold: none while no
// capable colonist is below the target, else RangeStandsFor the capable adults
// (every capable colonist, not only those under the target, so the count does
// not fall as colonists train). Unknown while the profiles or their skills are
// unread.
func TrainingStands(profiles domain.Fact[[]PawnProfile], research domain.Fact[ResearchFacts]) domain.Fact[int] {
	r, known := ReviewTraining(profiles, research).Value()
	if !known {
		return domain.Unknown[int]()
	}
	if r.Below == 0 {
		return domain.Known(0)
	}
	return domain.Known(RangeStandsFor(r.Capable))
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

// RangesWanted is the stands the plan's training range should hold: the stand
// count while the gap is open, else 0. A standing range stays and only grows;
// the plan never retires it.
func RangesWanted(stands domain.Fact[int]) int {
	if n, known := stands.Value(); known {
		return n
	}
	return 0
}
