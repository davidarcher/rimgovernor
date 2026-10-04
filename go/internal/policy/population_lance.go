package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LanceDefs are the worn items whose target verb downs a pawn with a
// hediff rather than violent damage (#1038): the psychic shock lance adds
// PsychicShock, so the target skips the death-on-downed roll a gun or blade
// would risk. The insanity lance drives a pawn berserk instead, so it is not
// a capture tool.
var LanceDefs = map[Resource]bool{"Apparel_PsychicShockLance": true}

// LanceUser is one colonist able to use a worn lance now: alive, standing,
// out of a mental state and capable of violence (the verb is violent).
type LanceUser struct {
	Pawn domain.PawnID
	Item string
}

// LanceChoice is one lance use: the colonist, the worn lance and the
// hostile it downs.
type LanceChoice struct {
	User, Target domain.PawnID
	Item         string
}

// lanceScore ranks a standing target by its skills: the sum of its
// non-disabled skill levels.
func lanceScore(p PrisonerProspect) int {
	total := 0
	for _, s := range p.Skills {
		if !s.Disabled {
			total += s.Level
		}
	}
	return total
}

// LanceTarget is LanceCandidate while some colonist wears a lance (f.Gear):
// the review's MaintainPopulation deficit.
func LanceTarget(f RoundsFacts) domain.PawnID {
	if !lanceWorn(f.Gear) {
		return ""
	}
	return LanceCandidate(f)
}

// LanceCandidate is the standing hostile humanlike a lance should down
// while the colony is below domain.PopulationTarget: known recruitable
// (#1034), not downed, not a prisoner, and the best skills; ties go to the
// lowest pawn ID. It is "" when the colony is at target, its size is
// unknown or no row qualifies. Who wears a lance is the planner's combat
// read (SelectLanceUse).
func LanceCandidate(f RoundsFacts) domain.PawnID {
	colony, known := f.PrisonerColony.Value()
	if !known || colony.Colonists >= domain.PopulationTarget {
		return ""
	}
	rows, known := f.Custody.Value()
	if !known {
		return ""
	}
	var best domain.PawnID
	bestScore := -1
	for _, row := range rows {
		dead, dk := row.Dead.Value()
		downed, wk := row.Downed.Value()
		hostile, hk := row.Hostile.Value()
		prisoner, pk := row.Prisoner.Value()
		recruitable, rk := row.Recruitable.Value()
		prospect, sk := row.Prospect.Value()
		if !dk || !wk || !hk || !pk || !rk || !sk || dead || downed || !hostile || prisoner || !recruitable {
			continue
		}
		score := lanceScore(prospect)
		if score > bestScore || score == bestScore && row.Pawn < best {
			best, bestScore = row.Pawn, score
		}
	}
	return best
}

// lanceWorn reports whether any observed colonist wears a lance.
func lanceWorn(gear domain.Fact[GearObservation]) bool {
	g, known := gear.Value()
	if !known {
		return false
	}
	for _, p := range g.Pawns {
		apparel, known := p.Apparel.Value()
		if !known {
			continue
		}
		for _, a := range apparel {
			if LanceDefs[a.Definition] {
				return true
			}
		}
	}
	return false
}

// SelectLanceUse pairs target with the lowest-ID able lance wearer; false
// when target is "" or nobody can use one now. Native checks the verb's
// charges, the target and the colonist again when the intent applies.
func SelectLanceUse(target domain.PawnID, users []LanceUser) (LanceChoice, bool) {
	if target == "" || len(users) == 0 {
		return LanceChoice{}, false
	}
	sorted := append([]LanceUser(nil), users...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Pawn < sorted[j].Pawn })
	for _, u := range sorted {
		if u.Pawn != target && u.Item != "" {
			return LanceChoice{User: u.Pawn, Target: target, Item: u.Item}, true
		}
	}
	return LanceChoice{}, false
}
