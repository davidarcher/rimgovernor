package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

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
		r.Target, r.Cell = "", peelerHome(view)
	}
	if r.Target == "" && stop.Kind == StopMeleeContact && gunner && !view.hostileDown(stop.Pawn) {
		r.Target, r.Cell = stop.Pawn, nil
	}
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
