package policy

import (
	"math"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DutyHarasser shoots a static target from range (hit-and-run): a siege
// camp from beyond its guns (#920), a crashed ship part (#1061), tribal
// archers from beyond their bows (#1055).
const DutyHarasser CombatDuty = "harasser"

// harassReach is the share of its weapon range a harasser stands off its
// target; it harasses only when that stand-off still clears every
// hostile's range.
const harassReach = 0.9

// tribalBowRange is a tribal's reach when its weapon range is unknown:
// a great bow's.
const tribalBowRange = 30

// harassTarget makes r a harasser of the target nearest at, standing off
// it at harassReach of reach. With back, a gunner already closer than
// that falls back along the same line to the stand-off.
func harassTarget(r *CombatRole, at domain.Cell, reach float64, targets []CombatPawnState, back bool) {
	target := targets[0]
	for _, h := range targets[1:] {
		c, _ := h.Cell.Value()
		b, _ := target.Cell.Value()
		if distance2(c, at) < distance2(b, at) {
			target = h
		}
	}
	want := harassReach * reach
	cell := standOffAt(target, at, want)
	if t, _ := target.Cell.Value(); back && cell == at && at != t {
		dx, dz := float64(at.X-t.X), float64(at.Z-t.Z)
		dist := math.Hypot(dx, dz)
		cell = domain.Cell{X: t.X + int32(math.Round(dx/dist*want)), Z: t.Z + int32(math.Round(dz/dist*want))}
	}
	r.Duty, r.Target, r.Cell = DutyHarasser, target.ID, &cell
}

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
		harassTarget(r, at, s.WeaponRange, camp, false)
	}
	return roles
}

// tribalStandoff is the siege stand-off generalized to tribals (#1055):
// while every live hostile with a known cell is a Tribal_ pawn, each free
// gunner whose gun reaches loadoutTribalRange and, at harassReach, still
// clears every tribal's range stands off its nearest tribal and shoots
// it. A gunner that does not outrange the bows and pila keeps its
// formation role.
func tribalStandoff(view CombatView, m *CombatMemory) {
	var tribals []CombatPawnState
	theirs := 0.0
	for _, h := range rankThreats(view) {
		if _, known := h.Cell.Value(); !known {
			continue
		}
		if !strings.HasPrefix(h.Kind, "Tribal_") {
			return
		}
		tribals = append(tribals, h)
		reach := h.WeaponRange
		if reach <= 0 {
			reach = tribalBowRange
		}
		theirs = max(theirs, reach)
	}
	if len(tribals) == 0 {
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		s := state[r.Pawn]
		at, known := s.Cell.Value()
		if !r.Ranged || r.Duty != "" || r.Mortar != nil || !known || s.WeaponRange < loadoutTribalRange || harassReach*s.WeaponRange <= theirs {
			continue
		}
		harassTarget(r, at, s.WeaponRange, tribals, true)
		r.Retreat = false
	}
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
		harassTarget(r, at, s.WeaponRange, parts, false)
		r.Retreat = false
	}
}
