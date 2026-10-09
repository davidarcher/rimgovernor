package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TacticShelter is the fight with no viable squad: nobody engages;
// every free colonist takes a verified retreat cell, a roofed room no
// hostile is in, or a verified step away on the defended side.
const TacticShelter CombatTactic = "shelter"

// shelterStep is how far, in cells, a colonist with no room to go to
// moves away from the nearest hostile.
const shelterStep = 12

// shelterRoles are the shelter tactic's moves; nil with no live hostile
// position known.
func shelterRoles(view CombatView, geometry GeometryReply, unreachable []domain.Cell) []CombatRole {
	return shelterRolesFor(view, geometry, unreachable, func(d SquadDefenderFacts) bool {
		busy, known := squadDraftBusy(d)
		return known && !busy
	})
}

// shelterRolesFor are the shelter moves of the defenders pick admits.
func shelterRolesFor(view CombatView, geometry GeometryReply, unreachable []domain.Cell, pick func(SquadDefenderFacts) bool) []CombatRole {
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
	if layout, known := view.Layout.Value(); known {
		safe = append(slices.Clone(layout.Retreat), safe...)
	}
	safe = slices.DeleteFunc(safe, func(c domain.Cell) bool { return !geometry.stands(c) || slices.Contains(unreachable, c) })
	at := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			at[p.ID] = c
		}
	}
	var roles []CombatRole
	var taken []domain.Cell
	for _, d := range view.Defenders {
		c, ok := at[d.ID]
		if !ok || !pick(d) || positive(d.Dead) || positive(d.Downed) || positive(d.MentalState) {
			continue
		}
		var cell domain.Cell
		if len(safe) > 0 {
			cell, safe = safe[0], safe[1:]
		} else {
			candidates := shelterChecks(view)
			candidates = slices.DeleteFunc(candidates, func(to domain.Cell) bool {
				return !geometry.stands(to) || slices.Contains(unreachable, to) || slices.Contains(taken, to) || keepDefended(view, c, to) != to || nearestDistance(to, hostiles) <= nearestDistance(c, hostiles)
			})
			if len(candidates) == 0 {
				continue
			}
			sort.SliceStable(candidates, func(i, j int) bool {
				return nearestDistance(candidates[i], hostiles) > nearestDistance(candidates[j], hostiles)
			})
			cell = candidates[0]
		}
		roles = append(roles, CombatRole{Pawn: d.ID, Cell: &cell, Retreat: true})
		taken = append(taken, cell)
	}
	return sortRoles(roles)
}

// ShelterReconsider identifies evidence for a new formation. Unknown pawn
// positions and weapon capabilities cannot establish a replacement. Arrival
// alone is useful shelter, not completion of the fight.
func ShelterReconsider(view CombatView, m CombatMemory) string {
	if m.Tactic != TacticShelter {
		return ""
	}
	for _, role := range m.Roles {
		if role.Cell != nil && slices.Contains(m.Unreachable, *role.Cell) {
			return "shelter_unreachable"
		}
	}
	if _, available := squadAssignments(view); available {
		return "defenders_available"
	}
	for _, role := range m.Roles {
		if role.Cell == nil {
			continue
		}
		for _, pawn := range view.Pawns {
			at, known := pawn.Cell.Value()
			if pawn.ID != role.Pawn || !known || at != *role.Cell {
				continue
			}
			for _, hostile := range view.Positional {
				cell, known := hostile.Position.Value()
				if !known || positive(hostile.Dead) || positive(hostile.Downed) {
					continue
				}
				reach := 1.5
				for _, state := range view.Pawns {
					if state.ID == domain.PawnID(hostile.ID) && state.WeaponRange > reach {
						reach = state.WeaponRange
					}
				}
				if math.Hypot(float64(at.X-cell.X), float64(at.Z-cell.Z)) <= reach {
					return "shelter_exposed"
				}
			}
		}
	}
	return ""
}

// shelterChecks names bounded candidates for the existing geometry read. A
// proposed vector is never an order until native confirms standability.
func shelterChecks(view CombatView) []domain.Cell {
	var cells []domain.Cell
	add := func(c domain.Cell) {
		if c.X >= 0 && c.Z >= 0 && len(cells) < maxGeometryCells/2 && !slices.Contains(cells, c) {
			cells = append(cells, c)
		}
	}
	if layout, known := view.Layout.Value(); known {
		for _, c := range layout.Retreat {
			add(c)
		}
	}
	for _, room := range view.Rooms {
		if room.Roofed {
			for _, c := range rectCells(room.Interior) {
				add(c)
			}
		}
	}
	for _, pawn := range view.Pawns {
		from, known := pawn.Cell.Value()
		if !known {
			continue
		}
		var hostiles []domain.Cell
		for _, h := range view.Positional {
			if c, known := h.Position.Value(); known && !positive(h.Dead) && !positive(h.Downed) {
				hostiles = append(hostiles, c)
			}
		}
		if len(hostiles) == 0 {
			continue
		}
		h := hostiles[nearestIndex(from, hostiles)]
		dx, dz := float64(from.X-h.X), float64(from.Z-h.Z)
		n := math.Hypot(dx, dz)
		if n == 0 {
			continue
		}
		to := keepDefended(view, from, domain.Cell{X: max(0, from.X+int32(math.Round(dx/n*shelterStep))), Z: max(0, from.Z+int32(math.Round(dz/n*shelterStep)))})
		add(to)
		for _, offset := range []domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			add(keepDefended(view, from, domain.Cell{X: to.X + offset.X, Z: to.Z + offset.Z}))
		}
	}
	return cells
}

// keepDefended pulls a fallback move target back onto the defended side:
// inside the room the colonist stands in, else, in a layout, no
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
