package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TacticShelter is the fight with no viable squad (#968): nobody engages;
// every free colonist takes a cell in a roofed room no hostile is in,
// farthest from the hostiles first, or, with no such room, steps
// shelterStep cells straight away from the nearest hostile.
const TacticShelter CombatTactic = "shelter"

// shelterStep is how far, in cells, a colonist with no room to go to
// moves away from the nearest hostile.
const shelterStep = 12

// shelterRoles are the shelter tactic's moves; nil with no live hostile
// position known.
func shelterRoles(view CombatView) []CombatRole {
	var hostiles []domain.Cell
	down := downPawns(view)
	for _, t := range view.Positional {
		if c, ok := t.Position.Value(); ok && !positive(t.Dead) && !positive(t.Downed) && !down[domain.PawnID(t.ID)] {
			hostiles = append(hostiles, c)
		}
	}
	if len(hostiles) == 0 {
		return nil
	}
	var safe []domain.Cell
	for _, r := range view.Rooms {
		if r.Roofed && !slices.ContainsFunc(hostiles, r.contains) {
			safe = append(safe, rectCells(r.Interior)...)
		}
	}
	sort.SliceStable(safe, func(i, j int) bool {
		di, dj := nearestDistance(safe[i], hostiles), nearestDistance(safe[j], hostiles)
		if di != dj {
			return di > dj
		}
		return cellLess(safe[i], safe[j])
	})
	at := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			at[p.ID] = c
		}
	}
	var roles []CombatRole
	for _, d := range view.Defenders {
		busy, bk := squadDraftBusy(d)
		c, ok := at[d.ID]
		if !ok || !bk || busy || positive(d.Dead) || positive(d.Downed) || positive(d.MentalState) {
			continue
		}
		var cell domain.Cell
		if len(safe) > 0 {
			cell, safe = safe[0], safe[1:]
		} else {
			h := hostiles[nearestIndex(c, hostiles)]
			dx, dz := float64(c.X-h.X), float64(c.Z-h.Z)
			n := math.Hypot(dx, dz)
			if n == 0 {
				continue
			}
			cell = domain.Cell{X: max(0, c.X+int32(math.Round(dx/n*shelterStep))), Z: max(0, c.Z+int32(math.Round(dz/n*shelterStep)))}
		}
		roles = append(roles, CombatRole{Pawn: d.ID, Cell: &cell, Retreat: true})
	}
	return sortRoles(roles)
}
