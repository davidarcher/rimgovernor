package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The sleeping layout of a starter shell before its ring goes up (#612):
// beds are the first construction on the site and the sleeping spots the
// interim, so the ring is raised around colonists who already have somewhere
// to lie down. The packing is the shelter interior template's
// (interior_shelter.go, #2042): one bunk slot per colonist, shared by the
// spot rung and the bed rung (a bed replaces the spot on the same cells), each
// a 1x2 footprint at the slot's rotation, off the shell's corner cells
// (a bed against a ring corner is reachable diagonally from outside the
// wall), the cell inside the door, the starter storage patch and the cells
// the plan digs. Fewer bunks than asked for is not an error: the shell is
// small, and whatever fits is placed.

// BunkRect is the footprint a bunk anchored at anchor occupies at a rotation.
func BunkRect(anchor domain.Cell, rot domain.Rotation) Rectangle {
	return OccupiedRect(anchor, shelterBunkSize, rot)
}

// BunkCells is the two cells of BunkRect.
func BunkCells(anchor domain.Cell, rot domain.Rotation) []domain.Cell {
	return rectCells(BunkRect(anchor, rot))
}

// ShellCornerCells returns the interior cells beside a corner of the ring:
// a wall cell that touches the interior only diagonally, which a rectangle
// has four of. A concave ring has more.
func ShellCornerCells(shell domain.RoomFootprint) []domain.Cell {
	interior := map[domain.Cell]bool{}
	for _, c := range shell.Interior() {
		interior[c] = true
	}
	corner := map[domain.Cell]bool{}
	for _, w := range shell.Walls() {
		orthogonal := false
		for _, n := range []domain.Cell{{X: w.X + 1, Z: w.Z}, {X: w.X - 1, Z: w.Z}, {X: w.X, Z: w.Z + 1}, {X: w.X, Z: w.Z - 1}} {
			orthogonal = orthogonal || interior[n]
		}
		if orthogonal {
			continue
		}
		for _, n := range []domain.Cell{{X: w.X + 1, Z: w.Z + 1}, {X: w.X - 1, Z: w.Z + 1}, {X: w.X + 1, Z: w.Z - 1}, {X: w.X - 1, Z: w.Z - 1}} {
			if interior[n] {
				corner[n] = true
			}
		}
	}
	var cells []domain.Cell
	for _, c := range shell.Interior() {
		if corner[c] {
			cells = append(cells, c)
		}
	}
	return cells
}

// ShelterRoom reads a starter layout as the shelter template's input: its
// interior, the door, and the cells the template keeps clear (the starter
// storage patch, the cells plan dig mines and reserved). False when the
// interior is not a rectangle the door opens onto.
func ShelterRoom(layout StarterLayout, shapes PieceShapes, occupants, campfires int, reserved []domain.Cell) (InteriorRoom, bool) {
	cells := layout.Shell.Interior()
	if len(cells) == 0 {
		return InteriorRoom{}, false
	}
	room := InteriorRoom{Role: RoomRoleShelter, Interior: cellsRectangle(cells), Doors: []domain.Cell{layout.Shell.Door()}, Shapes: shapes, Occupants: occupants, Campfires: campfires}
	if int64(len(cells)) != int64(room.Interior.Width)*int64(room.Interior.Height) {
		return InteriorRoom{}, false
	}
	if _, ok := doorSide(room.Interior, room.Doors[0]); !ok {
		return InteriorRoom{}, false
	}
	room.Reserved = append(append(append([]domain.Cell(nil), rectCells(layout.Storage)...), layout.Mined...), reserved...)
	return room, true
}

// PlanShelterBunks is the bunk slots of the layout's interior for up to
// occupants sleepers (0 fills every bunk that fits), in world cells, best
// first; reserved are further cells to keep clear.
func PlanShelterBunks(layout StarterLayout, shapes PieceShapes, occupants, campfires int, reserved []domain.Cell) []InteriorPiece {
	room, ok := ShelterRoom(layout, shapes, occupants, campfires, reserved)
	if !ok {
		return nil
	}
	plan, ok := PlanInterior(room, InteriorPieceDef{})
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

// BunkLayout picks the first layout whose interior holds every bunk
// footprint and whose corner cells hold none: the shell a later rung raises
// around the bunks an earlier one placed. ok is false when no layout does.
func BunkLayout(layouts []StarterLayout, bunks []Rectangle) (StarterLayout, bool) {
	for _, layout := range layouts {
		interior := map[domain.Cell]bool{}
		for _, c := range layout.Shell.Interior() {
			interior[c] = true
		}
		corner := map[domain.Cell]bool{}
		for _, c := range ShellCornerCells(layout.Shell) {
			corner[c] = true
		}
		fits := true
		for _, bunk := range bunks {
			for _, p := range rectCells(bunk) {
				fits = fits && interior[p] && !corner[p]
			}
		}
		if fits {
			return layout, true
		}
	}
	return StarterLayout{}, false
}
