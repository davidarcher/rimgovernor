package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const TacticDoorChoke CombatTactic = "door_choke"

// meleeDoorSite uses an observed room doorway when the established defense
// corridor is unavailable. Ranged threats, explosions, custody, fire and
// unknown physical door facts exclude this formation.
func meleeDoorSite(view CombatView) (RoomDoor, bool) {
	if view.Hunt || outmatched(view) || len(liveExploders(view)) > 0 {
		return RoomDoor{}, false
	}
	if layout, known := view.Layout.Value(); known {
		if _, choke := layout.Choke.Value(); choke {
			return RoomDoor{}, false
		}
	}
	if len(brawlers(view.Defenders)) == 0 || len(rankThreats(view)) == 0 || !slices.ContainsFunc(view.Defenders, func(d SquadDefenderFacts) bool { return squadDefenderEligible(d) && positive(d.RangedEquipped) }) {
		return RoomDoor{}, false
	}
	down := downPawns(view)
	for _, threat := range view.Threats {
		if positive(threat.Dead) || positive(threat.Downed) || down[domain.PawnID(threat.ID)] {
			continue
		}
		ranged, known := threat.RangedEquipped.Value()
		if threat.Building || !known || ranged {
			return RoomDoor{}, false
		}
	}
	doors, known := view.DoorStates.Value()
	if !known {
		return RoomDoor{}, false
	}
	var choices []RoomDoor
	for _, room := range view.Rooms {
		role, rk := room.Role.Value()
		burning, bk := room.Burning.Value()
		if !rk || !bk || burning || !room.Roofed || role == RoomRolePrisonCell || role == RoomRolePrisonBarracks || role == RoomRoleContainmentCell || role == RoomRoleIsolationRoom {
			continue
		}
		for _, cell := range room.Doors {
			at := slices.IndexFunc(doors, func(d RoomDoor) bool { return d.Cell == cell })
			if at < 0 {
				continue
			}
			door := doors[at]
			owned, pk := door.PlayerOwned.Value()
			forbidden, fk := door.Forbidden.Value()
			open, ok := door.Open.Value()
			_, hk := door.HoldOpen.Value()
			blocked, ck := door.BlockedOpen.Value()
			if !pk || !owned || !fk || forbidden || !ok || !hk || !ck || (blocked && !open) {
				continue
			}
			safe := true
			for _, other := range room.Doors {
				if other == cell {
					continue
				}
				i := slices.IndexFunc(doors, func(d RoomDoor) bool { return d.Cell == other })
				if i < 0 || !doorFactFalse(doors[i].Open) || !doorFactFalse(doors[i].HoldOpen) {
					safe = false
				}
			}
			inside := domain.Cell{X: 2*cell.X - door.Outside.X, Z: 2*cell.Z - door.Outside.Z}
			direction := domain.Cell{X: door.Outside.X - cell.X, Z: door.Outside.Z - cell.Z}
			if !room.contains(inside) || abs32(direction.X)+abs32(direction.Z) != 1 {
				continue
			}
			for _, hostile := range rankThreats(view) {
				at, known := hostile.Cell.Value()
				if !known || room.contains(at) || (at.X-cell.X)*direction.X+(at.Z-cell.Z)*direction.Z <= 0 {
					safe = false
				}
			}
			if safe {
				choices = append(choices, door)
			}
		}
	}
	sort.Slice(choices, func(i, j int) bool {
		di, dj := hostileDistance(view, choices[i].Cell), hostileDistance(view, choices[j].Cell)
		if di != dj {
			return di < dj
		}
		return cellLess(choices[i].Cell, choices[j].Cell)
	})
	if len(choices) == 0 {
		return RoomDoor{}, false
	}
	return choices[0], true
}

func doorFactFalse(f domain.Fact[bool]) bool { v, k := f.Value(); return k && !v }

func meleeDoorCells(view CombatView, door RoomDoor) ([]domain.Cell, []domain.Cell) {
	ranked := rankThreats(view)
	if len(ranked) == 0 {
		return nil, nil
	}
	from, ok := ranked[0].Cell.Value()
	if !ok {
		return nil, nil
	}
	blockers := insideDoor(view, door.Cell, from)
	inward := domain.Cell{X: door.Cell.X - door.Outside.X, Z: door.Cell.Z - door.Outside.Z}
	var support []domain.Cell
	for step := int32(3); step <= 4; step++ {
		for offset := int32(-2); offset <= 2; offset++ {
			cell := domain.Cell{X: door.Cell.X + step*inward.X + offset*inward.Z, Z: door.Cell.Z + step*inward.Z + offset*inward.X}
			if slices.ContainsFunc(view.Rooms, func(r CombatRoom) bool { return slices.Contains(r.Doors, door.Cell) && r.contains(cell) }) {
				support = append(support, cell)
			}
		}
	}
	return blockers, support
}

func meleeDoorAsk(view CombatView, door RoomDoor) *GeometryRequest {
	blockers, support := meleeDoorCells(view, door)
	reserve := domain.Cell{X: door.Cell.X + 2*(door.Cell.X-door.Outside.X), Z: door.Cell.Z + 2*(door.Cell.Z-door.Outside.Z)}
	ask := &GeometryRequest{Cells: append(append(blockers, support...), door.Cell, reserve)}
	for _, hostile := range rankThreats(view) {
		if len(ask.Hostiles) < maxGeometryHostiles {
			ask.Hostiles = append(ask.Hostiles, hostile.ID)
		}
	}
	return ask
}

func meleeDoorFormation(view CombatView, geometry GeometryReply, door RoomDoor, relieved []domain.PawnID) []CombatRole {
	blockers, support := meleeDoorCells(view, door)
	if len(blockers) == 0 || !geometry.stands(blockers[0]) || !geometry.stands(door.Cell) {
		return nil
	}
	blockers = slices.DeleteFunc(blockers, func(c domain.Cell) bool { return !geometry.stands(c) })
	support = slices.DeleteFunc(support, func(c domain.Cell) bool { return !geometry.stands(c) })
	pool := rotated(brawlers(view.Defenders), relieved)
	if len(pool) == 0 {
		return nil
	}
	target := rankThreats(view)[0].ID
	reserve := domain.Cell{X: door.Cell.X + 2*(door.Cell.X-door.Outside.X), Z: door.Cell.Z + 2*(door.Cell.Z-door.Outside.Z)}
	if geometry.stands(reserve) {
		blockers = append(blockers, reserve)
	}
	roles := brawlerRoles(view, view.Defenders, true, GeometryReply{Proposals: blockers, Standable: geometry.Standable}, relieved)
	for _, defender := range view.Defenders {
		if !squadDefenderEligible(defender) || !positive(defender.RangedEquipped) || len(support) == 0 {
			continue
		}
		cell := support[0]
		support = support[1:]
		roles = append(roles, CombatRole{Pawn: defender.ID, Cell: &cell, Target: target, Ranged: true})
	}
	return sortRoles(roles)
}

// meleeDoorTurn admits the choke only after its healthy, plan-owned blockers
// and supporting shooters stand in position. One shooter opens a closed door
// by walking onto it, then returns to its assigned cell on the next read.
func meleeDoorTurn(view CombatView, memory *CombatMemory) {
	door := memory.ChokeDoor
	if door == nil {
		return
	}
	states, known := view.DoorStates.Value()
	index := slices.IndexFunc(states, func(d RoomDoor) bool { return d.Cell == door.Cell })
	if !known || index < 0 || !combatDoorExists(view, door.Cell) {
		memory.ChokeDoor = nil
		return
	}
	observed := states[index]
	ready := memory.Tactic == TacticDoorChoke && !memory.Wait && !memory.Scattered
	blockers := 0
	for _, role := range memory.Roles {
		if role.Duty != DutyBlocker && !role.Ranged {
			continue
		}
		pawnIndex := slices.IndexFunc(view.Pawns, func(p CombatPawnState) bool { return p.ID == role.Pawn })
		defenderIndex := slices.IndexFunc(view.Defenders, func(p SquadDefenderFacts) bool { return p.ID == role.Pawn })
		if pawnIndex < 0 || defenderIndex < 0 || !squadDefenderEligible(view.Defenders[defenderIndex]) || !slices.Contains(view.Orderable, role.Pawn) || role.Retreat || role.Cell == nil {
			ready = false
			continue
		}
		pawn := view.Pawns[pawnIndex]
		at, known := pawn.Cell.Value()
		if role.Duty == DutyBlocker {
			blockers++
		}
		if role.Pawn != door.Opener && (!known || at != *role.Cell) {
			ready = false
		}
	}
	ready = ready && blockers > 0
	open, ok := observed.Open.Value()
	if !ok || !doorFactFalse(observed.Forbidden) {
		ready = false
	}
	if !open && hostileDistance(view, door.Cell) <= doorOpenRange*doorOpenRange {
		ready = false
	}
	mode := DoorClose
	if ready {
		mode = DoorHoldOpen
	}
	if mode != door.Mode {
		door.Mode, door.Sent = mode, false
	}
	if !ready || open {
		door.Opener = ""
		return
	}
	if hostileDistance(view, door.Cell) <= doorOpenRange*doorOpenRange {
		return
	}
	if door.Opener != "" {
		return
	}
	for _, role := range memory.Roles {
		if role.Ranged && slices.Contains(view.Orderable, role.Pawn) && !slices.Contains(memory.Unreachable, door.Cell) {
			door.Opener = role.Pawn
			return
		}
	}
}

// CombatDoorClears releases every latch this fight opened, even if a later
// formation dropped its door selection. Routine policy may then reopen safe
// logistics links; native decides when an unblocked door physically closes.
func CombatDoorClears(memory CombatMemory) []CombatOrder {
	cells := slices.Clone(memory.HeldDoors)
	sort.Slice(cells, func(i, j int) bool { return cellLess(cells[i], cells[j]) })
	var out []CombatOrder
	for _, cell := range cells {
		out = append(out, CombatOrder{Kind: OrderDoor, Cell: cell, Door: DoorClose, Reason: ReasonFormation})
	}
	return out
}
