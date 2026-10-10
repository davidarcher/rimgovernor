package policy

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The laboratory template: research benches in one row against the
// back wall with their interaction cells on the open floor in front. The
// benches are the piece being placed when it is a research bench (the 3x2
// simple or the 5x2 hi-tech bench), else the simple bench. The analyzer
// (RoomFurniture.Analyzer) has one reserved slot on the room's centre line,
// one row in front of the benches' worker row, clear of every interaction
// cell and within the facility's link distance of each bench; it is planned
// only when the room is deep enough to leave the entrance row open and the
// slot reaches every bench. Nothing else: a lab holds no filth-producing
// piece, and its sterile floor is the flooring policy's clean tier
// (flooring.go), not a template piece.

// labAnalyzerSlot names the analyzer's slot.
const labAnalyzerSlot = "analyzer"

func init() {
	RegisterInteriorTemplate(RoomRoleLaboratory, InteriorTemplate{Name: "laboratory", Plan: planLaboratory})
}

func planLaboratory(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	def, ok := BenchRowDef(f, piece, RoomRoleLaboratory)
	if !ok {
		return nil, false
	}
	benches, _, _, ok := BenchRow(f, def, 1, 0, func(i int) string { return fmt.Sprintf("bench.%d", i+1) })
	for i := range benches {
		benches[i].Row = "benches"
	}
	if !ok {
		return benches, ok
	}
	if analyzer, ok := labAnalyzer(f, benches, def); ok {
		benches = append(benches, analyzer)
	}
	return benches, true
}

// labAnalyzer is the analyzer's slot: centred on the room, in the two rows in
// front of the worker row, kept off row 0 so the entrance stays open, and
// close enough to every bench to link it.
func labAnalyzer(f InteriorFrame, benches []InteriorPiece, bench InteriorPieceDef) (InteriorPiece, bool) {
	link := f.Shapes.Furniture.Analyzer
	shape, ok := f.Shapes.Get(link.Def)
	if !ok || len(benches) == 0 {
		return InteriorPiece{}, false
	}
	// The worker row is the bench's interaction row; the analyzer stands in
	// front of it.
	v := f.Depth - bench.Size.Z - 1 - shape.Size.Z
	if v < 1 {
		return InteriorPiece{}, false
	}
	p := NewInteriorPiece(labAnalyzerSlot, shape.Def, shape.Size, domain.North, domain.Cell{X: CentreStart(f.Width, shape.Size.X), Z: v})
	p.Centred = true
	for _, b := range benches {
		if rectCentreDistance(p.Rect, b.Rect) > link.MaxDistance {
			return InteriorPiece{}, false
		}
	}
	return p, true
}

// rectCentreDistance is the straight distance between two rectangles' centres.
func rectCentreDistance(a, b Rectangle) float64 {
	dx := float64(2*a.X+a.Width-2*b.X-b.Width) / 2
	dz := float64(2*a.Z+a.Height-2*b.Z-b.Height) / 2
	return math.Sqrt(dx*dx + dz*dz)
}
