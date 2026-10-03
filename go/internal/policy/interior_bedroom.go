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
//
// Sizing by tier (#1214): the Camp/Masonry standard room is 3x4 and holds
// the bed and end table only; the 4x4 and 4x5 rooms of later tiers hold the
// full set. The set follows the room's size, so a wing that keeps its old
// size keeps its old set. No optional piece is placed that would take the
// room's space below bedroomMinSpace.

// Space stat terms: each interior tile adds bedroomSpacePerTile, each tile
// a blocking piece stands on takes bedroomSpacePerBlocked off again.
const (
	bedroomSpacePerTile    = 1.4
	bedroomSpacePerBlocked = 0.9
	bedroomMinSpace        = 12.5
	// bedroomFullSetTiles is the smallest interior that holds the full set;
	// smaller rooms get the bed and end table only.
	bedroomFullSetTiles = 16
)

// bedroomSpace is the planned space of a width x depth room with blocked
// tiles under furniture.
func bedroomSpace(width, depth int32, blocked int) float64 {
	return bedroomSpacePerTile*float64(width*depth) - bedroomSpacePerBlocked*float64(blocked)
}

func init() {
	RegisterInteriorTemplate(RoomRoleBedroom, InteriorTemplate{Name: "bedroom", Plan: planBedroom})
}

// planBedroom plans around the requested bed, else the room's standing
// bed, else a single Bed, each at its real size.
func planBedroom(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	if piece.Family == RoomRoleBedroom {
		return planBedroomWith(f, piece.Def, piece.Size)
	}
	for _, d := range f.Standing {
		if d.Family == RoomRoleBedroom {
			return planBedroomWith(f, d.Def, d.Size)
		}
	}
	bed, ok := f.Shapes.Get("Bed")
	if !ok {
		return nil, false
	}
	return planBedroomWith(f, bed.Def, bed.Size)
}

// planBedroomWith plans around a bed of the given definition and North
// size (Bed 1x2, DoubleBed 2x2).
func planBedroomWith(f InteriorFrame, bedDef string, size domain.Cell) ([]InteriorPiece, bool) {
	// The row inside the entrance stays floor.
	if f.Depth < size.Z+1 || f.Width < size.X {
		return nil, false
	}
	fs, ok := f.furnishings()
	if !ok {
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
		if bedroomSpace(f.Width, f.Depth, len(blocked)+len(cells)) < bedroomMinSpace {
			return false
		}
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
	tableLeft := x >= 1 && (right+fs.Dresser.Size.X <= f.Width || x < fs.Dresser.Size.X)
	tableU, dresserU := left, right
	if !tableLeft {
		tableU, dresserU = right, x-fs.Dresser.Size.X
	}
	try(NewInteriorPiece("end_table", endTableDef, fs.EndTable.Size, domain.South, domain.Cell{X: tableU, Z: back}))
	if f.Width*f.Depth < bedroomFullSetTiles {
		return pieces, true
	}
	try(NewInteriorPiece("dresser", dresserDef, fs.Dresser.Size, domain.South, domain.Cell{X: dresserU, Z: back}))
	for _, u := range []int32{f.Width - 1, 0} {
		if try(NewInteriorPiece("lamp", standingLampDef, fs.Lamp.Size, domain.South, domain.Cell{X: u, Z: back})) {
			break
		}
	}
	return pieces, true
}
