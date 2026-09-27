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
	case RoomRoleBarracks:
		return ModuleBarracks, true
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
	return domain.NewRoomFootprint(cells, r.Door, r.DoorRot)
}

// PlannedShells lists the plan's rooms for role as shells, in plan order;
// a room whose shell is invalid is skipped.
func (p LayoutPlan) PlannedShells(role RoomRole) []domain.RoomFootprint {
	want, ok := LayoutModule(role)
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
