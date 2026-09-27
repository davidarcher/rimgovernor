package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StopMeleeContact is the #849 stop for a melee job begun between a
// hostile and a colonist: the event's Pawn is the attacker, its Target
// the victim.
const StopMeleeContact CombatStopKind = "melee_contact"

// peelerHome is where the peeler waits (#865): one step behind the first
// gunner's inner-line cell (#860), clear of both lines, or nowhere (it
// waits where it was drafted) on a layout without an inner line.
func peelerHome(view CombatView) *domain.Cell {
	layout, ok := view.Layout.Value()
	if !ok || len(layout.Retreat) == 0 || len(layout.Firing) == 0 {
		return nil
	}
	f, r := layout.Firing[0], layout.Retreat[0]
	cell := domain.Cell{X: 2*r.X - f.X, Z: 2*r.Z - f.Z}
	if cell.X < 0 || cell.Z < 0 {
		return nil
	}
	return &cell
}

// peel is the reaction table's peel row (#865). On a melee contact stop
// by a hostile on a gunner, the peeler leaves its home to attack that
// hostile; once its target is down or dead it goes home. A peeler already
// on a live target keeps it.
func peel(view CombatView, stop StopEvent, m *CombatMemory) {
	peeler, gunner := -1, false
	for i, r := range m.Roles {
		if r.Duty == DutyPeeler {
			peeler = i
		}
		if r.Ranged && r.Pawn == stop.Target {
			gunner = true
		}
	}
	if peeler < 0 {
		return
	}
	r := &m.Roles[peeler]
	if r.Target != "" && view.hostileDown(r.Target) {
		r.Target, r.Cell = "", r.Home
	}
	if r.Target == "" && stop.Kind == StopMeleeContact && gunner && !view.hostileDown(stop.Pawn) {
		r.Target, r.Cell = stop.Pawn, nil
	}
}

// peelable is the one melee contact the peeler answers (#881): its live
// target; or, when it is free, the stop's attacker on a gunner, else the
// first hostile in melee on a gunner. Empty without a peeler.
func peelable(view CombatView, stop StopEvent, m CombatMemory) domain.PawnID {
	gunners := map[domain.PawnID]bool{}
	var peeler *CombatRole
	for i, r := range m.Roles {
		if r.Ranged {
			gunners[r.Pawn] = true
		}
		if r.Duty == DutyPeeler {
			peeler = &m.Roles[i]
		}
	}
	switch {
	case peeler == nil:
		return ""
	case peeler.Target != "" && !view.hostileDown(peeler.Target):
		return peeler.Target
	case stop.Kind == StopMeleeContact && gunners[stop.Target] && !view.hostileDown(stop.Pawn):
		return stop.Pawn
	}
	for _, p := range view.Pawns {
		if p.Stance == StanceMelee && gunners[p.Target] && !gunners[p.ID] && !p.Dead && !p.Downed {
			return p.ID
		}
	}
	return ""
}

// unpeeled is the positional threats less the contact the peeler answers:
// a melee attacker on a gunner stands on the cover row, and breaking that
// contact is the peeler's job, not a compromised hold (#881).
func unpeeled(view CombatView, stop StopEvent, m CombatMemory) []DefensiveThreatFacts {
	id := peelable(view, stop, m)
	if id == "" {
		return view.Positional
	}
	return slices.DeleteFunc(slices.Clone(view.Positional), func(t DefensiveThreatFacts) bool { return domain.PawnID(t.ID) == id })
}

// hostileDown reports a hostile known downed or dead.
func (v CombatView) hostileDown(id domain.PawnID) bool {
	for _, t := range v.Threats {
		if domain.PawnID(t.ID) == id && (positive(t.Dead) || positive(t.Downed)) {
			return true
		}
	}
	for _, p := range v.Pawns {
		if p.ID == id && (p.Dead || p.Downed) {
			return true
		}
	}
	return false
}
