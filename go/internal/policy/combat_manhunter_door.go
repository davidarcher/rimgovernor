package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Door potshot constants (#900), in cells.
const (
	// doorCloseRange: an animal this close to the door shuts it.
	doorCloseRange = 3
	// doorOpenRange: the nearest animal back beyond this reopens it.
	doorOpenRange = 6
	// doorGunners is how many gunners shoot from inside the door.
	doorGunners = 2
)

// manhunterDoor is the manhunter tactic's hit-and-run from a door (#900).
// On a formation without blockers, the planned-room door nearest the pack
// becomes the potshot door: the doorGunners gunners nearest it take the
// cells just inside it and the door is held open. Every stop after, an
// animal within doorCloseRange of the door closes it and the door gunners
// hold their cells without a target; the nearest animal back beyond
// doorOpenRange opens it again.
func manhunterDoor(view CombatView, formed bool, m *CombatMemory) {
	if m.Tactic != TacticManhunter {
		m.ManhunterDoor = nil
		return
	}
	if formed {
		m.ManhunterDoor = potshotDoor(view, m.Roles)
	}
	door := m.ManhunterDoor
	if door == nil {
		return
	}
	near := nearestAnimal(view, door.Cell)
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
	if door.Mode == DoorClose {
		for i := range m.Roles {
			if m.Roles[i].Duty == DutyDoorway {
				m.Roles[i].Target = ""
			}
		}
	}
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
	var gunners []int
	for i, r := range roles {
		if r.Ranged {
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
		roles[gunners[k]].Cell, roles[gunners[k]].Duty = &c, DutyDoorway
	}
	return &PodDoor{Cell: door, Mode: DoorHoldOpen}
}

// nearestAnimal is the squared distance from c to the nearest live
// hostile with a known cell.
func nearestAnimal(view CombatView, c domain.Cell) int64 {
	var cells []domain.Cell
	for _, h := range rankThreats(view) {
		if at, ok := h.Cell.Value(); ok {
			cells = append(cells, at)
		}
	}
	return nearestDistance(c, cells)
}
