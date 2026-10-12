package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Room construction to plan shapes: a room builder raises the
// planned room's exact rectangle and door instead of searching.

// PlannedRoleFor is the v2 plan role a builder's room role fills; false for
// a role the core plans no room for.
func PlannedRoleFor(role RoomRole) (PlannedRole, bool) {
	switch role {
	case RoomRoleBedroom:
		return PlannedBedroom, true
	case RoomRoleSuite:
		return PlannedSuite, true
	case RoomRoleShelter:
		return PlannedShelter, true
	case RoomRoleKitchen:
		return PlannedKitchen, true
	case RoomRoleDiningRoom:
		return PlannedDining, true
	case RoomRoleRecRoom:
		return PlannedRec, true
	case RoomRoleHospital:
		return PlannedHospital, true
	case RoomRolePrisonCell, RoomRolePrisonBarracks:
		return PlannedPrison, true
	case RoomRoleWorkshop:
		return PlannedWorkshop, true
	case RoomRoleStoreroom:
		return PlannedStorage, true
	case RoomRoleLaboratory:
		return PlannedLab, true
	case RoomRoleTomb:
		return PlannedTomb, true
	case RoomRoleThroneRoom:
		return PlannedThrone, true
	case RoomRoleNursery:
		return PlannedNursery, true
	case RoomRolePlayroom:
		return PlannedPlayroom, true
	case RoomRoleClassroom:
		return PlannedClassroom, true
	case RoomRoleWorshipRoom:
		return PlannedWorship, true
	case RoomRoleDeathrestChamber:
		return PlannedDeathrestChamber, true
	case RoomRoleContainmentCell:
		return PlannedContainmentCell, true
	case RoomRoleIsolationRoom:
		return PlannedIsolationRoom, true
	}
	return "", false
}

// PlannedShells lists the plan's rooms for role as shells, in plan order;
// a room whose shell is invalid is skipped.
func (p LayoutPlan) PlannedShells(role RoomRole) []domain.RoomFootprint {
	want, ok := PlannedRoleFor(role)
	if !ok {
		return nil
	}
	var shells []domain.RoomFootprint
	for _, r := range p.Rooms {
		if r.Role != want {
			continue
		}
		if shell, err := r.Footprint(); err == nil {
			shells = append(shells, shell)
		}
	}
	return shells
}

// NextPlannedRoom is the plan's first open-ground room of role with no
// ring standing yet: the kitchen, freezer or jail its owning
// goal shells before furnishing. A dug room is mined out first.
func (p LayoutPlan) NextPlannedRoom(role PlannedRole, ground GroundCensus) (PlannedRoom, bool) {
	rooms := p.Rooms
	if role == PlannedYard {
		// Yards are reservations viewed as rooms, not plan rooms.
		rooms = p.YardRooms()
	}
	for _, r := range rooms {
		if r.Role != role {
			continue
		}
		if !p.GroundMatches(r, ground) {
			return r, true
		}
	}
	return PlannedRoom{}, false
}
