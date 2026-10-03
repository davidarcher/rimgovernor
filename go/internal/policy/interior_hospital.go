package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The hospital template (#806): beds in one row, heads against the back
// wall, in pairs that share a vitals monitor between their heads. A
// VitalsMonitor must stand adjacent to the bed it serves (mustBePlacedAdjacent
// in Buildings_Misc.xml) and one monitor links every adjacent bed, so each
// pair is bed, monitor, bed with one free cell before the next pair. A
// bed's head is its anchor cell (BedUtility.GetSlotPos), so the beds face
// South: anchor and head on the back wall, feet toward the entrance. The
// sterile floor is the flooring policy's clean tier (flooring.go).

func init() {
	RegisterInteriorTemplate(RoomRoleHospital, InteriorTemplate{Name: "hospital", Plan: planHospital})
}

func planHospital(f InteriorFrame, _ InteriorPieceDef) ([]InteriorPiece, bool) {
	bedShape, bok := f.Shapes.Get(f.Shapes.Furniture.PrimaryBed())
	monitor, mok := f.Shapes.Get(f.Shapes.Furniture.Monitor.Def)
	// Bed (its rows) and at least the entrance row in front of it.
	if !bok || !mok || f.Depth < bedShape.Size.Z+1 {
		return nil, false
	}
	// A pair is bed, monitor, bed, with one free cell before the next.
	span := 2*bedShape.Size.X + monitor.Size.X
	pairs := RowCapacity(f.Width, span, 1)
	starts, ok := RowStarts(f.Width, span, 1, pairs, RowCentred)
	if !ok {
		return nil, false
	}
	var out []InteriorPiece
	for p, start := range starts {
		for side, u := range []int32{start, start + bedShape.Size.X + monitor.Size.X} {
			bed := NewInteriorPiece(fmt.Sprintf("bed.%d", 2*p+side+1), bedShape.Def, bedShape.Size, domain.South, domain.Cell{X: u, Z: f.Depth - bedShape.Size.Z})
			bed.Row = "beds"
			out = append(out, bed)
			if side == 0 {
				m := NewInteriorPiece(fmt.Sprintf("monitor.%d", p+1), monitor.Def, monitor.Size, domain.North, domain.Cell{X: start + bedShape.Size.X, Z: f.Depth - monitor.Size.Z})
				m.Row = "monitors"
				out = append(out, m)
			}
		}
	}
	return out, true
}
