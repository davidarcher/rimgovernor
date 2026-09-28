package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DutyHarasser shoots a static target from range (hit-and-run): a siege
// camp from beyond its guns (#920), a crashed ship part (#1061).
const DutyHarasser CombatDuty = "harasser"

// harassReach is the share of its weapon range a harasser stands off its
// target; it harasses only when that stand-off still clears every
// besieger's range.
const harassReach = 0.9

// harassRoles turns the siege gunners that outrange the camp into
// harassers (#920): each moves to the cell on the line from its nearest
// besieger toward itself at harassReach of its range and attacks that
// besieger. The harm makes the lord assault, and the raid_phase stop
// re-forms onto the killbox. Gunners that do not outrange keep their
// home role.
func harassRoles(view CombatView, roles []CombatRole) []CombatRole {
	besiegers := liveBesiegers(view)
	var camp []CombatPawnState
	theirs := 0.0
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
		if _, ok := besiegers[p.ID]; ok {
			if _, known := p.Cell.Value(); known {
				camp = append(camp, p)
				theirs = max(theirs, p.WeaponRange)
			}
		}
	}
	if len(camp) == 0 {
		return roles
	}
	for i := range roles {
		r := &roles[i]
		s := state[r.Pawn]
		at, known := s.Cell.Value()
		if !r.Ranged || !known || s.WeaponRange <= 0 || harassReach*s.WeaponRange <= theirs {
			continue
		}
		target := camp[0]
		for _, h := range camp[1:] {
			c, _ := h.Cell.Value()
			b, _ := target.Cell.Value()
			if distance2(c, at) < distance2(b, at) {
				target = h
			}
		}
		cell := standOffAt(target, at, harassReach*s.WeaponRange)
		r.Duty, r.Target, r.Cell = DutyHarasser, target.ID, &cell
	}
	return roles
}

// shipPartHitAndRun is hit-and-run on a crashed ship part (#1061): once
// no mech is left alive, every free gunner stands off the nearest ship
// part at harassReach of its range and shoots it. The part has no guns,
// so there is no outrange check; the stand-off keeps the gunner clear of
// any mechs the part wakes next.
func shipPartHitAndRun(view CombatView, m *CombatMemory) {
	var parts []CombatPawnState
	for _, s := range view.Structures {
		if strings.Contains(s.Def, "ShipPart") {
			parts = append(parts, CombatPawnState{ID: s.ID, Cell: domain.Known(s.Cell)})
		}
	}
	if len(parts) == 0 {
		return
	}
	for _, h := range rankThreats(view) {
		if strings.HasPrefix(h.Kind, "Mech_") {
			return
		}
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		s := state[r.Pawn]
		at, known := s.Cell.Value()
		if !r.Ranged || r.Duty != "" || r.Mortar != nil || !known || s.WeaponRange <= 0 {
			continue
		}
		target := parts[0]
		for _, p := range parts[1:] {
			c, _ := p.Cell.Value()
			b, _ := target.Cell.Value()
			if distance2(c, at) < distance2(b, at) {
				target = p
			}
		}
		cell := standOffAt(target, at, harassReach*s.WeaponRange)
		r.Duty, r.Target, r.Cell, r.Retreat = DutyHarasser, target.ID, &cell, false
	}
}
