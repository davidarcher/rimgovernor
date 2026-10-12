package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// RowCapacity is how many spans with the given gap fit along a length.
func RowCapacity(length, span, gap int32) int32 {
	if span < 1 || gap < 0 || length < span {
		return 0
	}
	return (length + gap) / (span + gap)
}

// NewInteriorPiece builds a piece whose footprint's lower corner is at corner.
func NewInteriorPiece(slot, def string, size domain.Cell, rot domain.Rotation, corner domain.Cell) InteriorPiece {
	w, h, _ := rotatedSize(size, rot)
	return InteriorPiece{Slot: slot, Def: def, Size: size, Rot: rot, Rect: Rectangle{X: corner.X, Z: corner.Z, Width: w, Height: h}}
}

// standingLampDef is the lamp beside a bed or throne. It is no
// facility (no row links it), so the catalog has no rule that names it.
const standingLampDef = "StandingLamp"

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
