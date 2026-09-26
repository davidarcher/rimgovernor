package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The bedroom template (#802): one bed on the centre line with its head
// against the back wall, an end table beside the head, a dresser on the
// other side of the head, and a standing lamp in a back corner. Everything
// hugs the back wall so the rest of the room stays clear floor, which is
// what the Space stat counts (1.4 per tile, 0.9 off per furniture tile).
//
// Game facts (RimWorld 1.5, BedUtility.GetSlotPos and
// CompAffectedByFacilities.CanPotentiallyLinkTo_Static):
//   - A bed's sleeping (head) cells are its anchor row; it extends toward
//     its facing direction, so a canonical South bed anchored on the back
//     wall has its head there and its feet toward the entrance.
//   - EndTable links only when cardinally adjacent to a head cell; Dresser
//     links within 6 cells centre to centre. Each links at most one per
//     bed (maxSimultaneous 1), so a second table or dresser adds nothing
//     and the template never mirrors them into a pair.
//
// Only the bed is required; the end table, dresser and lamp are placed in
// that order when they fit and keep the room walkable, so a cramped room
// gets a bare bed rather than no plan.

func init() {
	RegisterInteriorTemplate(RoomRoleBedroom, InteriorTemplate{Name: "bedroom", Plan: planBedroom})
}

var (
	bedSize       = domain.Cell{X: 1, Z: 2}
	endTableSize  = domain.Cell{X: 1, Z: 1}
	dresserSize   = domain.Cell{X: 2, Z: 1}
	standLampSize = domain.Cell{X: 1, Z: 1}
)

func planBedroom(f InteriorFrame, _ InteriorPieceDef) ([]InteriorPiece, bool) {
	return planBedroomWith(f, "Bed", bedSize)
}

// planBedroomWith plans around a bed of the given definition and North
// size (Bed 1x2, DoubleBed 2x2).
func planBedroomWith(f InteriorFrame, bedDef string, size domain.Cell) ([]InteriorPiece, bool) {
	// The row inside the entrance stays floor.
	if f.Depth < size.Z+1 || f.Width < size.X {
		return nil, false
	}
	back := f.Depth - 1
	x := CentreStart(f.Width, size.X)
	bed := NewInteriorPiece("bed", bedDef, size, domain.South, domain.Cell{X: x, Z: f.Depth - size.Z})
	bed.Centred = true
	pieces := []InteriorPiece{bed}

	room := InteriorRoom{Interior: Rectangle{Width: f.Width, Height: f.Depth}, Doors: f.Doors}
	blocked := map[domain.Cell]bool{}
	for _, c := range rectCells(bed.Rect) {
		blocked[c] = true
	}
	try := func(p InteriorPiece) bool {
		if !f.Contains(p.Rect) {
			return false
		}
		cells := rectCells(p.Rect)
		for _, c := range cells {
			if blocked[c] {
				return false
			}
		}
		if !InteriorPlacementWalkable(room, blocked, cells) {
			return false
		}
		for _, c := range cells {
			blocked[c] = true
		}
		pieces = append(pieces, p)
		return true
	}

	// End table on the entrance side of the head when there is room,
	// dresser on the far side; swap sides when only the near side fits a
	// dresser.
	left, right := x-1, x+size.X
	tableLeft := x >= 1 && (right+dresserSize.X <= f.Width || x < dresserSize.X)
	tableU, dresserU := left, right
	if !tableLeft {
		tableU, dresserU = right, x-dresserSize.X
	}
	try(NewInteriorPiece("end_table", "EndTable", endTableSize, domain.South, domain.Cell{X: tableU, Z: back}))
	try(NewInteriorPiece("dresser", "Dresser", dresserSize, domain.South, domain.Cell{X: dresserU, Z: back}))
	for _, u := range []int32{f.Width - 1, 0} {
		if try(NewInteriorPiece("lamp", "StandingLamp", standLampSize, domain.South, domain.Cell{X: u, Z: back})) {
			break
		}
	}
	return pieces, true
}
