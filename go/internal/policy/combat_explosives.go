package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Heavy explosives raids: once any live hostile carries an
// explosive weapon (threat tier 0) the line spreads two tiles apart
// (firingGap), takes its cells for room to move rather than cover, and
// posts outside the explosives' reach first, so gunners with the longer
// weapon focus the grenadiers from beyond their range. Shield-belt melee
// fighters charge the rocketeers.

// explosiveHostiles are the live hostiles carrying an explosive weapon,
// by threat rank.
func explosiveHostiles(view CombatView) []CombatPawnState {
	return slices.DeleteFunc(rankThreats(view), func(h CombatPawnState) bool {
		return threatTier(h, nil) != threatExplosive
	})
}

// rocketeer carries a launcher (an explosive weapon fired by a shoot verb)
// rather than a grenade.
func rocketeer(h CombatPawnState) bool { return h.WeaponFacts.Launcher }

// explosiveCells are Formation's candidate cells against explosives: the
// inner line joins the candidates, cover is not ranked, and the cells
// outside every explosive hostile's known range come first.
func explosiveCells(view CombatView, cells []domain.Cell, explosive []CombatPawnState) []domain.Cell {
	if layout, ok := view.Layout.Value(); ok {
		for _, c := range layout.Retreat {
			if !slices.Contains(cells, c) {
				cells = append(cells, c)
			}
		}
	}
	reached := func(c domain.Cell) bool {
		return slices.ContainsFunc(explosive, func(h CombatPawnState) bool {
			at, ok := h.Cell.Value()
			return ok && h.WeaponRange > 0 && math.Hypot(float64(c.X-at.X), float64(c.Z-at.Z)) <= h.WeaponRange
		})
	}
	out := slices.DeleteFunc(slices.Clone(cells), reached)
	for _, c := range cells {
		if reached(c) {
			out = append(out, c)
		}
	}
	return out
}

// explosivesCharge sends every shield-belt melee fighter at the nearest
// live rocketeer; the belt stops the shots on the way in and a launcher
// cannot fire at a pawn in melee with it.
func explosivesCharge(view CombatView, m *CombatMemory) {
	var rockets []CombatPawnState
	for _, h := range explosiveHostiles(view) {
		if rocketeer(h) {
			rockets = append(rockets, h)
		}
	}
	if len(rockets) == 0 {
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	for i, r := range m.Roles {
		s := state[r.Pawn]
		if !s.ShieldBelt || r.Ranged || r.Mortar != nil {
			continue
		}
		target := rockets[0].ID
		if at, ok := s.Cell.Value(); ok {
			best := int64(-1)
			for _, h := range rockets {
				if c, ok := h.Cell.Value(); ok && (best < 0 || distance2(at, c) < best) {
					target, best = h.ID, distance2(at, c)
				}
			}
		}
		m.Roles[i].Cell, m.Roles[i].Home, m.Roles[i].Duty, m.Roles[i].Retreat = nil, nil, "", false
		m.Roles[i].Target = target
	}
}
