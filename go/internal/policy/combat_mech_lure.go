package policy

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// mechRaid reports a raid of mechanoids: at least one live hostile, and
// every live hostile a Mech_ kind.
func mechRaid(view CombatView) bool {
	ranked := rankThreats(view)
	for _, h := range ranked {
		if !strings.HasPrefix(h.Kind, "Mech_") {
			return false
		}
	}
	return len(ranked) > 0
}

// markMechs is the view's positional facts with each Mech_ kind marked,
// so a mech raid's assault lord gets the hold (#922).
func markMechs(view CombatView) []DefensiveThreatFacts {
	mech := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		mech[p.ID] = strings.HasPrefix(p.Kind, "Mech_")
	}
	out := slices.Clone(view.Positional)
	for i := range out {
		out[i].Mech = mech[domain.PawnID(out[i].ID)]
	}
	return out
}

// mechLure is the hold's lure step against a mech raid (#922). While a
// live ranged mech outranges our longest gunner and no live mech is
// within that range of any firing cell, each gunner on a firing cell
// waits on its inner-line cell with no target, so the mechs walk in
// instead of shooting from beyond our reach. Once a mech comes within our
// range of the line, the lured gunners return to their firing cells and
// focus fire picks their targets. Lancers, outranged by rifles, never
// trigger it. Roles already fallen back (#860) are left alone.
func mechLure(view CombatView, m *CombatMemory) {
	layout, ok := view.Layout.Value()
	if m.Tactic != TacticHold || !ok || len(layout.Retreat) != len(layout.Firing) || len(layout.Retreat) == 0 || !mechRaid(view) {
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
