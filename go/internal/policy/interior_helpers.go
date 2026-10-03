package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Shared canonical-frame geometry for interior templates (#798 regularity:
// rows, centre line, mirror pairs). Every helper works in the frame of
// interior.go: u along the entrance wall, v away from it.

// InteriorWall is a wall of the canonical frame.
type InteriorWall int

const (
	// WallBack faces the entrance (v = Depth-1).
	WallBack InteriorWall = iota
	// WallLeft is u = 0, WallRight u = Width-1.
	WallLeft
	WallRight
	// WallFront holds the entrance (v = 0).
	WallFront
)

// Contains reports whether a canonical rectangle lies inside the frame.
func (f InteriorFrame) Contains(r Rectangle) bool {
	return r.Width > 0 && r.Height > 0 && r.X >= 0 && r.Z >= 0 && r.X+r.Width <= f.Width && r.Z+r.Height <= f.Depth
}

// CentreStart is where a span of cells starts to sit centred on a length;
// when the parities differ the span sits half a cell toward the start
// (the entrance side after the frame's mirror).
func CentreStart(length, span int32) int32 { return (length - span) / 2 }

// RowAnchor places a row of pieces along a wall.
type RowAnchor int

const (
	// RowCentred centres the row on the wall.
	RowCentred RowAnchor = iota
	// RowFromStart starts the row at u (or v) = 0.
	RowFromStart
)

// RowStarts lays count spans with equal gaps along a length and returns
// each span's start; false when they do not fit.
func RowStarts(length, span, gap, count int32, anchor RowAnchor) ([]int32, bool) {
	if span < 1 || gap < 0 || count < 1 {
		return nil, false
	}
	total := count*span + (count-1)*gap
	if total > length {
		return nil, false
	}
	start := int32(0)
	if anchor == RowCentred {
		start = CentreStart(length, total)
	}
	starts := make([]int32, count)
	for i := range starts {
		starts[i] = start + int32(i)*(span+gap)
	}
	return starts, true
}

// RowCapacity is how many spans with the given gap fit along a length.
func RowCapacity(length, span, gap int32) int32 {
	if span < 1 || gap < 0 || length < span {
		return 0
	}
	return (length + gap) / (span + gap)
}

// NewInteriorPiece builds a canonical piece whose footprint's lower corner
// is at corner.
func NewInteriorPiece(slot, def string, size domain.Cell, rot domain.Rotation, corner domain.Cell) InteriorPiece {
	w, h, _ := rotatedSize(size, rot)
	return InteriorPiece{Slot: slot, Def: def, Size: size, Rot: rot, Rect: Rectangle{X: corner.X, Z: corner.Z, Width: w, Height: h}}
}

// standingLampDef is the lamp beside a bed or throne (#802). It is no
// facility (no row links it), so the catalog has no rule that names it.
const standingLampDef = "StandingLamp"

// furnishings are the shapes of the furnishing levers beside a bed or
// throne (#802): an end table and a dresser, the bed's facilities
// (RoomFurniture), and a standing lamp.
type furnishings struct{ EndTable, Dresser, Lamp InteriorPieceDef }

// furnishings reads the levers from the frame's shapes; false when the
// catalog has no row for one.
func (f InteriorFrame) furnishings() (furnishings, bool) {
	var fs furnishings
	var a, b, c bool
	fs.EndTable, a = f.Shapes.Get(f.Shapes.Furniture.EndTable.Def)
	fs.Dresser, b = f.Shapes.Get(f.Shapes.Furniture.Dresser.Def)
	fs.Lamp, c = f.Shapes.Get(standingLampDef)
	return fs, a && b && c
}

// BenchRowDef is the definition a family's template plans: the piece being
// placed when the family holds it, else the family's default bench
// (RoomFurniture.Bench).
func BenchRowDef(f InteriorFrame, piece InteriorPieceDef, family RoomRole) (InteriorPieceDef, bool) {
	if piece.Family == family {
		return piece, true
	}
	return f.Shapes.Get(f.Shapes.Furniture.BenchFor(family))
}

// frontInteraction reports whether a bench's interaction cell is the open
// floor in front of it at North (the row below its footprint), where a bench
// row leaves its worker row.
func frontInteraction(def InteriorPieceDef) bool {
	if def.Interaction == nil {
		return false
	}
	r := OccupiedRect(domain.Cell{}, def.Size, domain.North)
	o := *def.Interaction
	return o.Z == r.Z-1 && o.X >= r.X && o.X < r.X+r.Width
}

// BenchRow lays def's benches in one centred row against the back wall,
// facing the entrance, with their interaction cells on the floor in front
// (#820). Every slot is pitch wide, the widest of def and the family
// members standing in the room, and each bench stands centred in its slot,
// so a room holding mixed widths keeps one back line, one rotation and
// even spacing. The row needs its interaction row and one open row before
// the entrance; limit caps the count (0 for none). It returns the benches
// and each slot's start and the pitch.
func BenchRow(f InteriorFrame, def InteriorPieceDef, gap, limit int32, slot func(i int) string) ([]InteriorPiece, []int32, int32, bool) {
	if !frontInteraction(def) || f.Depth < def.Size.Z+2 {
		return nil, nil, 0, false
	}
	pitch := def.Size.X
	for _, s := range f.Standing {
		if s.Family == def.Family {
			pitch = max(pitch, s.Size.X)
		}
	}
	n := RowCapacity(f.Width, pitch, gap)
	if limit > 0 {
		n = min(n, limit)
	}
	starts, ok := RowStarts(f.Width, pitch, gap, n, RowCentred)
	if !ok {
		return nil, nil, 0, false
	}
	var out []InteriorPiece
	for i, u := range starts {
		off := *def.Interaction
		p := NewInteriorPiece(slot(i), def.Def, def.Size, domain.North, domain.Cell{X: u + (pitch-def.Size.X)/2, Z: f.Depth - def.Size.Z})
		p.InteractionOffset = &off
		out = append(out, p)
	}
	return out, starts, pitch, true
}

// AisleRows are the rows of a double-sided room (the battery room, the
// tomb): a piece on each side of a 1-cell aisle straight in from the door,
// one every pitch cells of depth, nearest the door first.
func AisleRows(depth, pitch int32) []int32 {
	var out []int32
	for v := int32(0); pitch > 0 && v < depth; v += pitch {
		out = append(out, v)
	}
	return out
}
