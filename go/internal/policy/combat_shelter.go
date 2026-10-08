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
	return shelterRolesFor(view, func(d SquadDefenderFacts) bool {
		busy, known := squadDraftBusy(d)
		return known && !busy
	})
}

// shelterRolesFor are the shelter moves of the defenders pick admits.
func shelterRolesFor(view CombatView, pick func(SquadDefenderFacts) bool) []CombatRole {
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
		c, ok := at[d.ID]
		if !ok || !pick(d) || positive(d.Dead) || positive(d.Downed) || positive(d.MentalState) {
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
			cell = keepDefended(view, c, cell)
		}
		roles = append(roles, CombatRole{Pawn: d.ID, Cell: &cell, Retreat: true})
	}
	return sortRoles(roles)
}

// keepDefended pulls a fallback move target back onto the defended side
// (#2376): inside the room the colonist stands in, else, in a layout, no
// further than the firing line's far side. A colonist already outside both
// keeps the unclamped target; there is no defended side to hold.
func keepDefended(view CombatView, from, to domain.Cell) domain.Cell {
	for _, r := range view.Rooms {
		if !r.contains(from) || r.contains(to) {
			continue
		}
		in := r.Interior
		return domain.Cell{
			X: min(max(to.X, in.X), in.X+in.Width-1),
			Z: min(max(to.Z, in.Z), in.Z+in.Height-1),
		}
	}
	layout, ok := view.Layout.Value()
	if !ok || !BehindFiringLine(layout.Firing, layout.Toward, from) || BehindFiringLine(layout.Firing, layout.Toward, to) {
		return to
	}
	dx, dz := float64(to.X-from.X), float64(to.Z-from.Z)
	for t := 1 - 1.0/shelterStep; t > 0; t -= 1.0 / shelterStep {
		c := domain.Cell{X: from.X + int32(math.Round(dx*t)), Z: from.Z + int32(math.Round(dz*t))}
		if BehindFiringLine(layout.Firing, layout.Toward, c) {
			return c
		}
	}
	return from
}
