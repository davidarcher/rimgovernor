package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Room construction to plan shapes (#787, C3): a room builder raises the
// planned room's exact rectangle and door instead of searching.

// LayoutModule is the v2 plan role a builder's room role fills; false for
// a role the core plans no room for.
func LayoutModule(role RoomRole) (ModuleRole, bool) {
	switch role {
	case RoomRoleBedroom:
		return ModuleBedroom, true
	case RoomRoleSuite:
		return ModuleSuite, true
	case RoomRoleBarracks:
		return ModuleBarracks, true
	case RoomRoleShelter:
		return ModuleShelter, true
	case RoomRoleKitchen:
		return ModuleKitchen, true
	case RoomRoleDiningRoom:
		return ModuleDining, true
	case RoomRoleRecRoom:
		return ModuleRec, true
	case RoomRoleHospital:
		return ModuleHospital, true
	case RoomRolePrisonCell, RoomRolePrisonBarracks:
		return ModulePrison, true
	case RoomRoleWorkshop:
		return ModuleWorkshop, true
	case RoomRoleStoreroom:
		return ModuleStorage, true
	case RoomRoleLaboratory:
		return ModuleLab, true
	case RoomRoleTomb:
		return ModuleTomb, true
	case RoomRoleThroneRoom:
		return ModuleThrone, true
	case RoomRoleNursery:
		return ModuleNursery, true
	case RoomRolePlayroom:
		return ModulePlayroom, true
	case RoomRoleClassroom:
		return ModuleClassroom, true
	case RoomRoleWorshipRoom:
		return ModuleWorship, true
	case RoomRoleDeathrestChamber:
		return ModuleDeathrestChamber, true
	case RoomRoleContainmentCell:
		return ModuleContainmentCell, true
	case RoomRoleIsolationRoom:
		return ModuleIsolationRoom, true
	}
	return "", false
}

// PlannedRoomStanding is the census room standing enclosed on the planned
// room's centre cell and no larger than its interior.
func PlannedRoomStanding(r LayoutRoom, rooms RoomObservation) (Room, bool) {
	centre := domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}
	for _, room := range rooms.Rooms {
		if len(room.Cells) > int(r.Interior.Width*r.Interior.Height) {
			continue
		}
		if enclosed, known := room.Enclosed.Value(); !known || !enclosed {
			continue
		}
		for _, c := range room.Cells {
			if c == centre {
				return room, true
			}
		}
	}
	return Room{}, false
}

// Footprint is the room's shell: its interior walled round, the door where
// the plan put it.
func (r LayoutRoom) Footprint() (domain.RoomFootprint, error) {
	in := r.Interior
	cells := make([]domain.Cell, 0, int(in.Width*in.Height))
	for z := in.Z; z < in.Z+in.Height; z++ {
		for x := in.X; x < in.X+in.Width; x++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	var extra []domain.RoomDoor
	for _, d := range r.Doors {
		extra = append(extra, domain.RoomDoor{Cell: d.Cell, Entrance: d.Rot})
	}
	return domain.NewRoomFootprint(cells, r.Door, r.DoorRot, extra...)
}

// PlannedShells lists the plan's rooms for role as shells, in plan order;
// a room whose shell is invalid is skipped.
func (p LayoutPlan) PlannedShells(role RoomRole) []domain.RoomFootprint {
	want, ok := LayoutModule(role)
	if !ok {
		return nil
	}
	var shells []domain.RoomFootprint
	for _, r := range p.AllRooms() {
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
// room standing in it yet (#835): the kitchen, freezer or jail its owning
// goal shells before furnishing. A dug room is mined out first (#836).
func (p LayoutPlan) NextPlannedRoom(role ModuleRole, rooms RoomObservation) (LayoutRoom, bool) {
	for _, r := range p.AllRooms() {
		if r.Role != role {
			continue
		}
		if _, ok := PlannedRoomStanding(r, rooms); !ok {
			return r, true
		}
	}
	return LayoutRoom{}, false
}

// ShellDoors is the cells of r's ring that take a door rather than a
// wall: its own doors, its Link, and any other room's Link that lies in
// r's ring (the kitchen's side of the freezer door, #835).
func (p LayoutPlan) ShellDoors(r LayoutRoom) []domain.Cell {
	doors := []domain.Cell{r.Door}
	for _, d := range r.Doors {
		doors = append(doors, d.Cell)
	}
	if r.Link != nil {
		doors = append(doors, *r.Link)
	}
	in := r.Interior
	for _, o := range p.AllRooms() {
		if o.Link == nil || o.Interior == in {
			continue
		}
		l := *o.Link
		onX := l.X == in.X-1 || l.X == in.X+in.Width
		onZ := l.Z == in.Z-1 || l.Z == in.Z+in.Height
		if onX && l.Z >= in.Z && l.Z < in.Z+in.Height || onZ && l.X >= in.X && l.X < in.X+in.Width {
			doors = append(doors, l)
		}
	}
	return doors
}
