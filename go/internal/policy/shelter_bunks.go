package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The sleeping layout of a starter shell before its ring goes up:
// beds are the first construction on the site and the sleeping spots the
// interim, so the ring is raised around colonists who already have somewhere
// to lie down. The packing is the shelter interior template's
// (interior_shelter.go): one bunk slot per colonist, shared by the
// spot rung and the bed rung (a bed replaces the spot on the same cells), each
// a 1x2 footprint at the slot's rotation, off the cell inside the door, the
// starter storage patch and the cells the plan digs. Fewer bunks than asked for is not an error: the shell is
// small, and whatever fits is placed.

// BunkRect is the footprint a bunk anchored at anchor occupies at a rotation.
func BunkRect(anchor domain.Cell, rot domain.Rotation) Rectangle {
	return OccupiedRect(anchor, shelterBunkSize, rot)
}

// BunkCells is the two cells of BunkRect.
func BunkCells(anchor domain.Cell, rot domain.Rotation) []domain.Cell {
	return rectCells(BunkRect(anchor, rot))
}

// ShelterRoom reads a planned shelter room as the shelter template's input: its
// interior, the door, and the cells the template keeps clear (the starter
// storage patch, the cells plan dig mines and reserved). False when the
// interior is not a rectangle the door opens onto.
func ShelterRoom(room PlannedRoom, mined []domain.Cell, shapes PieceShapes, occupants, campfires, coolers int, reserved []domain.Cell) (InteriorRoom, bool) {
	shell, err := room.Footprint()
	if err != nil || room.Interior.Width <= 0 || room.Interior.Height <= 0 {
		return InteriorRoom{}, false
	}
	interior := InteriorRoom{Role: RoomRoleShelter, Interior: room.Interior, Doors: []domain.Cell{room.Door}, Shapes: shapes, Occupants: occupants, Campfires: campfires, Coolers: coolers}
	if _, ok := doorSide(interior.Interior, room.Door); !ok {
		return InteriorRoom{}, false
	}
	interior.Reserved = append(append(append([]domain.Cell(nil), rectCells(starterStorage(shell))...), mined...), reserved...)
	return interior, true
}

// PlanShelterBunks is the bunk slots of the room's interior for up to
// occupants sleepers (0 fills every bunk that fits), in world cells, best
// first; mined are the cells plan dig still mines and reserved further cells
// to keep clear.
func PlanShelterBunks(room PlannedRoom, mined []domain.Cell, shapes PieceShapes, occupants, campfires, coolers int, reserved []domain.Cell) []InteriorPiece {
	interior, ok := ShelterRoom(room, mined, shapes, occupants, campfires, coolers, reserved)
	if !ok {
		return nil
	}
	plan, ok := PlanInterior(interior, InteriorPieceDef{})
	if !ok {
		return nil
	}
	var bunks []InteriorPiece
	for _, p := range plan.Pieces {
		if p.IsBunk() {
			bunks = append(bunks, p)
		}
	}
	return bunks
}
