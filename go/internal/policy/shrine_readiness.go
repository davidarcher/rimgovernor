package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Breaching a sealed shrine releases its guards at once, so the gate is
// judged before the wall goes, never during the fight (#457). Every hold is
// a journal reason; Ready names the wall and the squad the breach goal
// (#458) drafts. Peaceful softens the gate: that storyteller never spawns
// hostile mechanoids (1.3+), so the squad and trap floors drop.
const (
	ShrineHoldNotSealed     = "not_sealed"
	ShrineHoldNoBreachWall  = "no_breach_wall"
	ShrineHoldEmergency     = "emergency_active"
	ShrineHoldSquadTooSmall = "squad_too_small"
	ShrineHoldNoRanged      = "no_ranged"
	ShrineHoldNoTraps       = "no_traps"
	ShrineHoldThreatUnknown = "threat_unknown"
	ShrineHoldThreatTooHigh = "threat_too_high"

	shrineSquadMinimum         = 2
	shrineSquadMinimumPeaceful = 1
	shrineRangedMinimumRange   = 20.0
	shrineTrapMinimum          = 3
	shrineTrapRadius           = 12.0
)

// shrineThreatCeiling is the most raid points a squad of the given size
// breaches under; larger squads read the last row.
var shrineThreatCeiling = []struct {
	squad  int
	points float64
}{{2, 300}, {3, 500}, {4, 800}}

// ShrineDefenderFacts is one colonist the breach could draft: the squad
// eligibility SelectSquadDefense uses plus the longest range the pawn's
// ranged weapon reaches (unknown or zero for a melee pawn).
type ShrineDefenderFacts struct {
	SquadDefenderFacts
	WeaponRange domain.Fact[float64]
}

type ShrineReadinessRequest struct {
	Shrine AncientShrine
	// RaidPoints is the storyteller's current reading (#395); unknown holds.
	RaidPoints domain.Fact[float64]
	// Peaceful is true under the Peaceful storyteller; unknown counts as not.
	Peaceful domain.Fact[bool]
	Squad    []ShrineDefenderFacts
	// Traps are the built spike traps' cells.
	Traps  []domain.Cell
	Center domain.Cell
	// Emergency is any active emergency fact; a breach never starts under one.
	Emergency bool
}

type ShrineReadiness struct {
	Ready  bool
	Reason string
	// Wall is the chosen breach wall: the candidate nearest the colony
	// centre. Squad is every eligible defender, ranged first.
	Wall  ShrineBreachWall
	Squad []domain.PawnID
	// Traps counts the built traps within shrineTrapRadius of the wall's
	// outside cell.
	Traps int
}

func shrineDefenderEligible(d ShrineDefenderFacts) bool {
	if !squadDefenderEligible(d.SquadDefenderFacts) {
		return false
	}
	armed, ok := d.Armed.Value()
	return ok && armed
}

func shrineRanged(d ShrineDefenderFacts) bool {
	ranged, rk := d.RangedEquipped.Value()
	reach, ok := d.WeaponRange.Value()
	return rk && ranged && ok && !math.IsNaN(reach) && reach >= shrineRangedMinimumRange
}

// ShrineBreachReadiness judges one shrine. It is a decision, never an
// order; the breach goal re-reads the facts before drafting anyone.
func ShrineBreachReadiness(r ShrineReadinessRequest) ShrineReadiness {
	out := ShrineReadiness{}
	if !r.Shrine.Sealed {
		out.Reason = ShrineHoldNotSealed
		return out
	}
	if len(r.Shrine.BreachWalls) == 0 {
		out.Reason = ShrineHoldNoBreachWall
		return out
	}
	walls := append([]ShrineBreachWall(nil), r.Shrine.BreachWalls...)
	sort.Slice(walls, func(i, j int) bool {
		a, b := squaredDistance(walls[i].Outside, r.Center), squaredDistance(walls[j].Outside, r.Center)
		if a != b {
			return a < b
		}
		return walls[i].EntityID < walls[j].EntityID
	})
	out.Wall = walls[0]
	for _, trap := range r.Traps {
		if math.Sqrt(float64(squaredDistance(trap, out.Wall.Outside))) <= shrineTrapRadius {
			out.Traps++
		}
	}
	peaceful, _ := r.Peaceful.Value()
	ranged := 0
	var shooters, others []domain.PawnID
	for _, d := range r.Squad {
		if !shrineDefenderEligible(d) {
			continue
		}
		if shrineRanged(d) {
			ranged++
			shooters = append(shooters, d.ID)
		} else {
			others = append(others, d.ID)
		}
	}
	sort.Slice(shooters, func(i, j int) bool { return shooters[i] < shooters[j] })
	sort.Slice(others, func(i, j int) bool { return others[i] < others[j] })
	out.Squad = append(shooters, others...)
	if r.Emergency {
		out.Reason = ShrineHoldEmergency
		return out
	}
	minimum, traps := shrineSquadMinimum, shrineTrapMinimum
	if peaceful {
		minimum, traps = shrineSquadMinimumPeaceful, 0
	}
	switch {
	case len(out.Squad) < minimum:
		out.Reason = ShrineHoldSquadTooSmall
	case ranged == 0:
		out.Reason = ShrineHoldNoRanged
	case out.Traps < traps:
		out.Reason = ShrineHoldNoTraps
	}
	if out.Reason != "" {
		return out
	}
	points, known := r.RaidPoints.Value()
	if !known || math.IsNaN(points) || math.IsInf(points, 0) {
		if peaceful {
			out.Ready = true
			return out
		}
		out.Reason = ShrineHoldThreatUnknown
		return out
	}
	ceiling := shrineThreatCeiling[len(shrineThreatCeiling)-1].points
	for _, row := range shrineThreatCeiling {
		if len(out.Squad) <= row.squad {
			ceiling = row.points
			break
		}
	}
	if points > ceiling && !peaceful {
		out.Reason = ShrineHoldThreatTooHigh
		return out
	}
	out.Ready = true
	return out
}
