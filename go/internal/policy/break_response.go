package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

const BreakExclusionRadius = 8

// AggressiveBreak uses the native aggression fact, never an inferred mood risk
// or a closed list that silently misses modded mental states.
// Social fights resolve through native ticks without a subdue response;
// treating them as combat would hold the clock that must resolve them.
func AggressiveBreak(p EmergencyPawn) bool {
	state, known := p.MentalState.Value()
	dead, dk := p.Dead.Value()
	down, wk := p.Downed.Value()
	return known && state.IsAggro && state.DefName != "SocialFighting" && dk && wk && !dead && !down
}

type BreakResponder struct {
	SquadDefenderFacts
	Cell domain.Fact[domain.Cell]
}

// SelectBreakSquad chooses at most two healthy melee responders. Distance is
// primary; stable pawn identity breaks ties. SUBDUE selects a legal blunt verb.
func SelectBreakSquad(target EmergencyPawn, cell domain.Fact[domain.Cell], candidates []BreakResponder) []domain.PawnID {
	at, known := cell.Value()
	if !known || !AggressiveBreak(target) {
		return nil
	}
	var pool []BreakResponder
	for _, c := range candidates {
		ranged, rk := c.RangedEquipped.Value()
		melee, mk := c.MeleeEquipped.Value()
		armed, ak := c.Armed.Value()
		_, ck := c.Cell.Value()
		if c.ID != domain.PawnID(target.ID) && squadDefenderEligible(c.SquadDefenderFacts) && mk && melee && rk && !ranged && ak && armed && ck {
			pool = append(pool, c)
		}
	}
	distance := func(c BreakResponder) int64 {
		p, _ := c.Cell.Value()
		dx, dz := int64(p.X)-int64(at.X), int64(p.Z)-int64(at.Z)
		return dx*dx + dz*dz
	}
	sort.Slice(pool, func(i, j int) bool {
		a, b := distance(pool[i]), distance(pool[j])
		if a != b {
			return a < b
		}
		return pool[i].ID < pool[j].ID
	})
	var out []domain.PawnID
	for _, c := range pool[:min(2, len(pool))] {
		out = append(out, c.ID)
	}
	return out
}

func InBreakRadius(cell, target domain.Cell) bool {
	dx, dz := int64(cell.X)-int64(target.X), int64(cell.Z)-int64(target.Z)
	return dx*dx+dz*dz <= BreakExclusionRadius*BreakExclusionRadius
}
