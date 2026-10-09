package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Door potshot constants, in cells.
const (
	// doorCloseRange: a hostile this close to the door shuts it.
	doorCloseRange = 3
	// doorOpenRange: the nearest hostile back beyond this reopens it.
	doorOpenRange = 6
	// doorGunners is how many gunners shoot from inside the door.
	doorGunners = 2
)

// OrderRepair sends a drafted pawn to repair a damaged door.
const OrderRepair CombatOrderKind = "repair"

// ReasonRepair is a door repair order.
const ReasonRepair CombatOrderReason = "repair"

// doorPotshot is the hit-and-run from a perimeter door, for a manhunter
// pack, a humanoid raid on squad defense and a hunt once
// its prey turns manhunter or hunts a colonist (the door stays once
// chosen); a killbox hold fights from its firing line instead.
// On a formation without blockers, the planned-room door nearest the hostiles
// becomes the potshot door: the doorGunners gunners nearest it take the
// cells just inside it and the door is held open. Every stop after, a
// hostile within doorCloseRange of the door closes it and the door gunners
// hold their cells without a target; the nearest hostile back beyond
// doorOpenRange opens it again.
func doorPotshot(view CombatView, formed bool, m *CombatMemory) {
	if m.Tactic != TacticManhunter && m.Tactic != TacticSquad && !(m.Tactic == TacticHunt && (m.PotshotDoor != nil || provokedPrey(view))) {
		m.PotshotDoor = nil
		return
	}
	if formed {
		m.PotshotDoor = potshotDoor(view, m.Roles)
	}
	door := m.PotshotDoor
	if door == nil {
		return
	}
	if !combatDoorExists(view, door.Cell) {
		m.PotshotDoor = nil
		for i := range m.Roles {
			if m.Roles[i].Duty == DutyDoorway {
				m.Roles[i].Target = ""
			}
		}
		return
	}
	near := hostileDistance(view, door.Cell)
	mode := door.Mode
	switch {
	case near <= doorCloseRange*doorCloseRange:
		mode = DoorClose
	case near > doorOpenRange*doorOpenRange:
		mode = DoorHoldOpen
	}
	if mode != door.Mode {
		door.Mode, door.Sent = mode, false
	}
	door.Repairer = ""
	if near > doorOpenRange*doorOpenRange && slices.Contains(view.DamagedDoors, door.Cell) {
		door.Repairer = doorRepairer(view, m.Roles, door.Cell)
	}
	if door.Mode == DoorClose {
		for i := range m.Roles {
			if m.Roles[i].Duty == DutyDoorway {
				m.Roles[i].Target = ""
			}
		}
	}
}

// combatDoorExists uses the current native room boundary, never the saved
// layout or a door retained by an earlier combat stop.
func combatDoorExists(view CombatView, cell domain.Cell) bool {
	if doors, known := view.DoorStates.Value(); known {
		return slices.ContainsFunc(doors, func(d RoomDoor) bool { return d.Cell == cell })
	}
	return slices.ContainsFunc(view.Rooms, func(room CombatRoom) bool {
		return slices.Contains(room.Doors, cell)
	})
}

// provokedPrey is a live prey that turned manhunter or is hunting a
// colonist: the hunt falls back to the door loop.
func provokedPrey(view CombatView) bool {
	live := map[domain.PawnID]bool{}
	for _, p := range rankThreats(view) {
		live[p.ID] = true
	}
	for _, t := range view.Threats {
		if live[domain.PawnID(t.ID)] && (positive(t.Manhunter) || positive(t.Hunting)) {
			return true
		}
	}
	return false
}

// doorRepairer is the door gunner nearest the door with a known cell,
// or none.
func doorRepairer(view CombatView, roles []CombatRole, door domain.Cell) domain.PawnID {
	var best domain.PawnID
	var bestD int64
	for _, p := range view.Pawns {
		c, ok := p.Cell.Value()
		if !ok || !slices.ContainsFunc(roles, func(r CombatRole) bool { return r.Pawn == p.ID && r.Duty == DutyDoorway }) {
			continue
		}
		if d := distance2(c, door); best == "" || d < bestD {
			best, bestD = p.ID, d
		}
	}
	return best
}

// potshotDoor picks the door and posts the door gunners, or nil: a
// formation with blockers, no planned door, or no gunner has none.
func potshotDoor(view CombatView, roles []CombatRole) *PodDoor {
	if slices.ContainsFunc(roles, func(r CombatRole) bool { return r.Duty == DutyBlocker }) {
		return nil
	}
	var doors []domain.Cell
	for _, r := range view.Rooms {
		doors = append(doors, r.Doors...)
	}
	var pack []domain.Cell
	for _, h := range rankThreats(view) {
		if c, ok := h.Cell.Value(); ok {
			pack = append(pack, c)
		}
	}
	if len(doors) == 0 || len(pack) == 0 {
		return nil
	}
	sort.SliceStable(doors, func(i, j int) bool { return nearestDistance(doors[i], pack) < nearestDistance(doors[j], pack) })
	door := doors[0]
	cells := insideDoor(view, door, pack[nearestIndex(door, pack)])
	at := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			at[p.ID] = c
		}
	}
	// A gunner is a ranged role or, on squad defense against melee raiders,
	// a defender carrying a ranged weapon: it shoots from the door
	// instead of charging.
	armed := map[domain.PawnID]bool{}
	for _, d := range view.Defenders {
		armed[d.ID] = positive(d.RangedEquipped)
	}
	var gunners []int
	for i, r := range roles {
		if r.Ranged || armed[r.Pawn] {
			gunners = append(gunners, i)
		}
	}
	sort.SliceStable(gunners, func(a, b int) bool {
		ca, oka := at[roles[gunners[a]].Pawn]
		cb, okb := at[roles[gunners[b]].Pawn]
		if oka != okb {
			return oka
		}
		return distance2(ca, door) < distance2(cb, door)
	})
	n := min(doorGunners, len(cells), len(gunners))
	if n == 0 {
		return nil
	}
	for k := range n {
		c := cells[k]
		roles[gunners[k]].Cell, roles[gunners[k]].Duty, roles[gunners[k]].Ranged = &c, DutyDoorway, true
	}
	return &PodDoor{Cell: door, Mode: DoorHoldOpen}
}

// hostileDistance is the squared distance from c to the nearest live
// hostile with a known cell.
func hostileDistance(view CombatView, c domain.Cell) int64 {
	var cells []domain.Cell
	for _, h := range rankThreats(view) {
		if at, ok := h.Cell.Value(); ok {
			cells = append(cells, at)
		}
	}
	return nearestDistance(c, cells)
}
