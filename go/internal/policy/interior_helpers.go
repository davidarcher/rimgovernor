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

// Thresholds are the floor cells just inside each door.
func (f InteriorFrame) Thresholds() []domain.Cell {
	var cells []domain.Cell
	for _, d := range f.Doors {
		c := d
		switch {
		case d.Z < 0:
			c.Z = 0
		case d.Z >= f.Depth:
			c.Z = f.Depth - 1
		case d.X < 0:
			c.X = 0
		default:
			c.X = f.Width - 1
		}
		cells = append(cells, c)
	}
	return cells
}

// WallBand is the strip thickness cells deep along a wall.
func (f InteriorFrame) WallBand(w InteriorWall, thickness int32) Rectangle {
	switch w {
	case WallLeft:
		return Rectangle{X: 0, Z: 0, Width: thickness, Height: f.Depth}
	case WallRight:
		return Rectangle{X: f.Width - thickness, Z: 0, Width: thickness, Height: f.Depth}
	case WallFront:
		return Rectangle{X: 0, Z: 0, Width: f.Width, Height: thickness}
	}
	return Rectangle{X: 0, Z: f.Depth - thickness, Width: f.Width, Height: thickness}
}

// CentreStart is where a span of cells starts to sit centred on a length;
// when the parities differ the span sits half a cell toward the start
// (the entrance side after the frame's mirror).
func CentreStart(length, span int32) int32 { return (length - span) / 2 }

// MirrorStart is where the mirror image of a span starting at start lies.
func MirrorStart(length, start, span int32) int32 { return length - start - span }

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

// BenchRowDef is the definition a family's template plans: the piece being
// placed when the family holds it, else the family's default.
func BenchRowDef(piece InteriorPieceDef, family, fallback string) InteriorPieceDef {
	if piece.Family == family {
		return piece
	}
	return InteriorPieceDefFor(fallback)
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
	if def.Interaction == nil || f.Depth < def.Size.Z+2 {
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

// MirrorPiece is a piece's mirror image across the frame's centre line,
// under a new slot name.
func (f InteriorFrame) MirrorPiece(p InteriorPiece, slot string) InteriorPiece {
	p.Slot = slot
	p.Rect.X = MirrorStart(f.Width, p.Rect.X, p.Rect.Width)
	if p.Rot == domain.East || p.Rot == domain.West {
		p.Rot = rotateCW(p.Rot, 2)
	}
	return p
}
