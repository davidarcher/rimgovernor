package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DoorChange is a desired vanilla latch setting. Passage is separate: a
// closed held door still needs a pawn to open it. To is the cooler room's
// adjacent floor cell, used only for emergency heat relief.
type DoorChange struct {
	Door                    RoomDoor
	HoldOpen, Heat, Passage bool
	To                      domain.Cell
	Occupants               []domain.PawnID
}

// DoorChanges owns routine door settings. Only measured internal traffic
// links are held open; emergency heat relief may use another safe internal
// link. Physical barriers, custody, perishables and fire separation prevail.
// Combat owns its doors while its Incident is active.
func DoorChanges(fact domain.Fact[RoomObservation], traffic domain.Fact[RoutesObservation], p RoundsPolicy) []DoorChange {
	observed, known := fact.Value()
	if !known {
		return nil
	}
	travelled := map[domain.Cell]bool{}
	if routes, ok := traffic.Value(); ok {
		for _, row := range routes.Traffic {
			if row.Layer == TrafficColonist && row.Samples > 0 {
				travelled[row.Cell] = true
			}
		}
	}
	rooms := slices.Clone(observed.Rooms)
	sort.Slice(rooms, func(i, j int) bool { return rooms[i].ID < rooms[j].ID })
	seen := map[domain.Cell]bool{}
	var changes []DoorChange
	for _, room := range rooms {
		doors := slices.Clone(room.Doors)
		sort.Slice(doors, func(i, j int) bool {
			if doors[i].Cell.X != doors[j].Cell.X {
				return doors[i].Cell.X < doors[j].Cell.X
			}
			return doors[i].Cell.Z < doors[j].Cell.Z
		})
		for _, door := range doors {
			if seen[door.Cell] {
				continue
			}
			seen[door.Cell] = true
			owned, pk := door.PlayerOwned.Value()
			held, hk := door.HoldOpen.Value()
			open, ok := door.Open.Value()
			forbidden, fk := door.Forbidden.Value()
			if !pk || !owned || !hk || !ok || !fk {
				continue
			}
			change := DoorChange{Door: door}
			far, exists := doorDestination(rooms, door)
			_, blockedKnown := door.BlockedOpen.Value()
			if exists && blockedKnown && !forbidden && safeDoorRoom(room) && safeDoorRoom(far) {
				here, ht := room.Temperature.Value()
				there, tt := far.Temperature.Value()
				if ht && tt {
					// Use the existing colony heat band, retaining an active opening
					// until recovery rather than oscillating at the entry threshold.
					threshold := p.HotEnter
					if held {
						threshold = p.HotExit
					}
					hot, cool := room, far
					from, to := here, there
					if there > here {
						hot, cool, from, to = far, room, there, here
					}
					hotRole, _ := hot.Role.Value()
					if from > threshold && to < from && to >= p.ColdExit && !positive(cool.TemperatureControl) &&
						!(hotRole == RoomRoleStoreroom && positive(hot.TemperatureControl)) && (len(hot.Pawns) > 0 || len(hot.Beds) > 0) {
						change.HoldOpen, change.Heat, change.Passage = true, true, !open
						change.Occupants = slices.Clone(hot.Pawns)
						if hot.ID == room.ID {
							change.To = door.Outside
						} else {
							change.To = domain.Cell{X: 2*door.Cell.X - door.Outside.X, Z: 2*door.Cell.Z - door.Outside.Z}
						}
					} else if travelled[door.Cell] && logisticsRoom(room) && logisticsRoom(far) &&
						!positive(room.TemperatureControl) && !positive(far.TemperatureControl) &&
						here >= p.ColdExit && here <= p.HotExit && there >= p.ColdExit && there <= p.HotExit {
						change.HoldOpen, change.Passage = true, !open
					}
				}
			}
			if held != change.HoldOpen || change.Passage {
				changes = append(changes, change)
			}
		}
	}
	sort.Slice(changes, func(i, j int) bool {
		if changes[i].HoldOpen != changes[j].HoldOpen {
			return !changes[i].HoldOpen
		}
		if changes[i].Heat != changes[j].Heat {
			return changes[i].Heat
		}
		if changes[i].Door.Cell.X != changes[j].Door.Cell.X {
			return changes[i].Door.Cell.X < changes[j].Door.Cell.X
		}
		return changes[i].Door.Cell.Z < changes[j].Door.Cell.Z
	})
	return changes
}

func doorDestination(rooms []Room, door RoomDoor) (Room, bool) {
	outside, known := door.Outdoors.Value()
	if !known || outside {
		return Room{}, false
	}
	for _, room := range rooms {
		if slices.Contains(room.Cells, door.Outside) {
			return room, true
		}
	}
	return Room{}, false
}

func safeDoorRoom(room Room) bool {
	role, rk := room.Role.Value()
	enclosed, ek := room.Enclosed.Value()
	burning, bk := room.Burning.Value()
	perishable, pk := room.PerishableContents.Value()
	_, tk := room.TemperatureControl.Value()
	secure := true
	for _, door := range room.Doors {
		outdoor, known := door.Outdoors.Value()
		if !known || outdoor && (!doorFactFalse(door.Open) || !doorFactFalse(door.HoldOpen)) {
			secure = false
		}
	}
	return secure && rk && ek && enclosed && bk && !burning && pk && !perishable && tk &&
		role != RoomRolePrisonCell && role != RoomRolePrisonBarracks && role != RoomRoleContainmentCell && role != RoomRoleIsolationRoom && role != RoomRoleTomb
}

func logisticsRoom(room Room) bool {
	role, _ := room.Role.Value()
	return role == RoomRoleWorkshop || role == RoomRoleStoreroom || role == RoomRoleLaboratory || role == RoomRoleRoom || role == RoomRoleNone
}
