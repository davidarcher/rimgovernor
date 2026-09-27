package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// StopSeriousInjury is the #849 stop for a colonist's serious hit.
const StopSeriousInjury CombatStopKind = "serious_injury"

// fallBack is the reaction table's pull-back row (#860). On a held line
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
