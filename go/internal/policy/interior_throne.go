package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The throne room template: the throne centred against the back
// wall facing the entrance, then the bedroom's furnishing levers (end
// table and dresser beside it, a standing lamp in a back corner) in the
// slots the room quality closer fills. The throne's footprint is the piece
// being placed (the native catalog's size, passed in by NextThroneStep);
// asked for no piece (the closer's furnishing plan) the template plans the
// furniture around a one-cell throne, and a standing throne's cells keep
// the closer's placements off it.

// throneSlot names the throne in the template.
const throneSlot = "throne"

// RequiredPiece is one piece a title's room requirement asks for besides the
// throne, planned in the slot (ThroneNeed.requiredPieces names them).
type RequiredPiece struct {
	Slot  string
	Piece InteriorPieceDef
}

func init() {
	RegisterInteriorTemplate(RoomRoleThroneRoom, InteriorTemplate{Name: "throne", Plan: planThrone})
}

func planThrone(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	fs, ok := f.furnishings()
	if !ok {
		return nil, false
	}
	size := piece.Size
	span := int32(1)
	var pieces []InteriorPiece
	room := InteriorRoom{Interior: Rectangle{Width: f.Width, Height: f.Depth}, Doors: f.Doors}
	blocked := map[domain.Cell]bool{}
	if piece.Def != "" && size.X > 0 && size.Z > 0 {
		// The row inside the entrance stays floor.
		if f.Depth < size.Z+1 || f.Width < size.X {
			return nil, false
		}
		span = size.X
		throne := NewInteriorPiece(throneSlot, piece.Def, size, domain.South, domain.Cell{X: CentreStart(f.Width, size.X), Z: f.Depth - size.Z})
		throne.Centred = true
		pieces = append(pieces, throne)
		for _, c := range rectCells(throne.Rect) {
			blocked[c] = true
		}
	}
	back, x := f.Depth-1, CentreStart(f.Width, span)
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
	// The title's required pieces (InteriorFrame.Required) take the side
	// walls, alternating left and right, every other row from the back
	// first so the floor between them stays walkable, before the
	// furnishing levers take what is left. A piece no wall cell fits is
	// left out of the plan.
	var rows []int32
	for z := f.Depth - 2; z >= 1; z -= 2 {
		rows = append(rows, z)
	}
	for z := f.Depth - 3; z >= 1; z -= 2 {
		rows = append(rows, z)
	}
	for _, req := range f.Required {
		sz := req.Piece.Size
		if sz.X <= 0 || sz.Z <= 0 {
			continue
		}
	place:
		for _, z := range rows {
			for _, u := range []int32{0, f.Width - sz.X} {
				if try(NewInteriorPiece(req.Slot, req.Piece.Def, sz, domain.South, domain.Cell{X: u, Z: z})) {
					break place
				}
			}
		}
	}
	try(NewInteriorPiece("end_table", fs.EndTable.Def, fs.EndTable.Size, domain.South, domain.Cell{X: x - 1, Z: back}))
	try(NewInteriorPiece("dresser", fs.Dresser.Def, fs.Dresser.Size, domain.South, domain.Cell{X: x + span, Z: back}))
	for _, u := range []int32{f.Width - 1, 0} {
		if try(NewInteriorPiece("lamp", standingLampDef, fs.Lamp.Size, domain.South, domain.Cell{X: u, Z: back})) {
			break
		}
	}
	return pieces, len(pieces) > 0
}
