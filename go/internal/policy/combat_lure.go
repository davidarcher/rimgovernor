package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// lure is the hold's lure step against any outranging ranged raid:
// mechs or sniper parties. While a live hostile outranges
// our longest gunner and no live hostile is within that range of any
// firing cell, each gunner on a firing cell waits in cover on its
// inner-line cell with no target, so the raiders walk in instead of
// shooting from beyond our reach. Once a hostile comes within our range
// of the line, the lured gunners return to their firing cells and focus
// fire picks their targets. A raid we outrange never triggers it. Roles
// already fallen back are left alone.
func lure(view CombatView, m *CombatMemory) {
	layout, ok := view.Layout.Value()
	if m.Tactic != TacticHold || !ok || len(layout.Retreat) != len(layout.Firing) || len(layout.Retreat) == 0 {
		m.MechLure = false
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	ours := 0.0
	for _, r := range m.Roles {
		if r.Ranged {
			ours = max(ours, state[r.Pawn].WeaponRange)
		}
	}
	outranged, near := false, false
	for _, h := range rankThreats(view) {
		outranged = outranged || ours > 0 && h.WeaponRange > ours
		c, ok := h.Cell.Value()
		for _, f := range layout.Firing {
			near = near || ok && math.Hypot(float64(c.X-f.X), float64(c.Z-f.Z)) <= ours
		}
	}
	lure := outranged && !near
	if !lure && !m.MechLure {
		return
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		if !r.Ranged || r.Retreat || r.Cell == nil {
			continue
		}
		for j := range layout.Firing {
			switch {
			case lure && *r.Cell == layout.Firing[j]:
				c := layout.Retreat[j]
				r.Cell, r.Target = &c, ""
			case lure && *r.Cell == layout.Retreat[j]:
				r.Target = ""
			case !lure && *r.Cell == layout.Retreat[j]:
				c := layout.Firing[j]
				r.Cell = &c
			default:
				continue
			}
			break
		}
	}
	m.MechLure = lure
}
