package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// scatterCells is how far a scattering defender runs from the raid.
const scatterCells = 20

// scatter is the hold's last resort. The line collapses when the
// fight is outmatched and most of the hold-the-line cells defenders once
// stood on are lost (their defenders downed or pushed off): every live
// role then drops its target and runs scatterCells away from the live
// hostiles, and the damaged doors are queued. The fight stays scattered
// while it is outmatched; once it is not, it drops its roles to re-form,
// and each queued door still damaged goes to the nearest free role to
// repair.
func scatter(view CombatView, m *CombatMemory) {
	at := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			at[p.ID] = c
		}
	}
	if !m.Scattered {
		layout, ok := view.Layout.Value()
		if !ok || m.Tactic != TacticHold {
			queueRepairs(view, m, at)
			return
		}
		live := view.live()
		held := 0
		for _, c := range holdLine(layout, *m) {
			for id, p := range at {
				if p == c && live[id] {
					held++
					break
				}
			}
		}
		m.LineHeld = max(m.LineHeld, held)
		if !outmatched(view) || m.LineHeld == 0 || 2*held >= m.LineHeld {
			queueRepairs(view, m, at)
			return
		}
		m.Scattered = true
		for _, d := range view.DamagedDoors {
			if !slices.Contains(m.Repairs, d) {
				m.Repairs = append(m.Repairs, d)
			}
		}
	} else if !outmatched(view) {
		m.Scattered, m.LineHeld, m.Roles = false, 0, nil
		return
	}
	var hx, hz float64
	n := 0
	down := downPawns(view)
	for _, t := range view.Threats {
		c, ok := at[domain.PawnID(t.ID)]
		if !ok || t.Building || positive(t.Dead) || positive(t.Downed) || down[domain.PawnID(t.ID)] {
			continue
		}
		hx, hz, n = hx+float64(c.X), hz+float64(c.Z), n+1
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		r.Target, r.Duty, r.Cell, r.Home, r.Mortar, r.Aim, r.Ground, r.Repair = "", "", nil, nil, nil, nil, nil, nil
		from, ok := at[r.Pawn]
		if !ok || n == 0 {
			continue
		}
		dx, dz := float64(from.X)-hx/float64(n), float64(from.Z)-hz/float64(n)
		l := math.Hypot(dx, dz)
		if l == 0 {
			dx, dz, l = 0, 1, 1
		}
		c := domain.Cell{X: max(0, from.X+int32(math.Round(dx/l*scatterCells))), Z: max(0, from.Z+int32(math.Round(dz/l*scatterCells)))}
		r.Cell, r.Retreat = &c, true
	}
	m.Kiter, m.Leading, m.PotshotDoor = "", false, nil
	m.Intercept, m.Rushing, m.MechLure = false, false, false
}

// queueRepairs drops the queued doors no longer damaged and gives each one
// still damaged to the nearest role with a known cell and no repair yet.
func queueRepairs(view CombatView, m *CombatMemory, at map[domain.PawnID]domain.Cell) {
	m.Repairs = slices.DeleteFunc(m.Repairs, func(d domain.Cell) bool { return !slices.Contains(view.DamagedDoors, d) })
	for i := range m.Roles {
		if r := &m.Roles[i]; r.Repair != nil && !slices.Contains(m.Repairs, *r.Repair) {
			r.Repair = nil
		}
	}
	for _, d := range m.Repairs {
		if slices.ContainsFunc(m.Roles, func(r CombatRole) bool { return r.Repair != nil && *r.Repair == d }) {
			continue
		}
		best := -1
		for i, r := range m.Roles {
			c, ok := at[r.Pawn]
			if r.Repair != nil || !ok {
				continue
			}
			if best < 0 || distance2(c, d) < distance2(at[m.Roles[best].Pawn], d) {
				best = i
			}
		}
		if best >= 0 {
			door := d
			m.Roles[best].Repair = &door
		}
	}
}

// StopSeriousInjury is the combat stop for a colonist's serious hit.
const StopSeriousInjury CombatStopKind = "serious_injury"

// fallBack is the reaction table's pull-back row. On a held line
// whose layout records an inner line: a breach or a compromised line pulls
// every defender back to its Retreat cell, and a serious injury pulls the
// hurt defender back alone. A role once pulled back stays on the inner
// line, which becomes the line reform judges; a line already fallen back
// has no further line, so its compromise or breach re-forms as before. It
// reports whether any role changed; a changed role is the stop's reaction
// instead of a re-formation.
func fallBack(view CombatView, stop StopEvent, m *CombatMemory) bool {
	layout, ok := view.Layout.Value()
	if m.Tactic != TacticHold || !ok || len(layout.Retreat) == 0 || len(layout.Retreat) != len(layout.Firing) || fellBack(*m) {
		return false
	}
	var who func(CombatRole) bool
	switch {
	case stop.Kind == StopBreach || HoldCompromised(layout.Firing, layout.Toward, unpeeled(view, stop, *m)):
		who = func(CombatRole) bool { return true }
	case stop.Kind == StopSeriousInjury && stop.Pawn != "":
		who = func(r CombatRole) bool { return r.Pawn == stop.Pawn }
	default:
		return false
	}
	taken := map[domain.Cell]bool{}
	for _, r := range m.Roles {
		if r.Retreat && r.Cell != nil {
			taken[*r.Cell] = true
		}
	}
	changed := false
	for i := range m.Roles {
		r := &m.Roles[i]
		if r.Retreat || !who(*r) {
			continue
		}
		if cell, ok := retreatCell(layout, r.Cell, taken); ok {
			r.Cell, r.Retreat = &cell, true
			changed = true
		}
	}
	return changed
}

// retreatCell is the inner-line cell behind from: its own Retreat cell when
// from is a firing cell, else (a proposed cover cell, or none) the first
// Retreat cell not yet taken.
func retreatCell(layout CombatLayout, from *domain.Cell, taken map[domain.Cell]bool) (domain.Cell, bool) {
	if from != nil {
		for i, f := range layout.Firing {
			if f == *from && !taken[layout.Retreat[i]] {
				taken[layout.Retreat[i]] = true
				return layout.Retreat[i], true
			}
		}
	}
	for _, c := range layout.Retreat {
		if !taken[c] {
			taken[c] = true
			return c, true
		}
	}
	return domain.Cell{}, false
}

// fellBack reports a formation whose every role is on the inner line.
func fellBack(m CombatMemory) bool {
	for _, r := range m.Roles {
		if !r.Retreat {
			return false
		}
	}
	return len(m.Roles) > 0
}

// holdLine is the line a hold's compromise is judged against: the inner
// line once the whole formation fell back to it, else the firing line.
func holdLine(layout CombatLayout, m CombatMemory) []domain.Cell {
	if fellBack(m) && len(layout.Retreat) > 0 {
		return layout.Retreat
	}
	return layout.Firing
}
