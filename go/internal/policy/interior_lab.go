package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The laboratory template (#806): research benches in one row against the
// back wall with their interaction cells on the open floor in front.
// Nothing else: a lab holds no filth-producing piece, and its sterile floor
// is the flooring policy's clean tier (flooring.go), not a template piece.

func init() {
	RegisterInteriorTemplate(RoomRoleLaboratory, InteriorTemplate{Name: "laboratory", Plan: planLaboratory})
}

// SimpleResearchBench at rotation North (Buildings_Production.xml).
var (
	researchBenchSize        = domain.Cell{X: 3, Z: 2}
	researchBenchInteraction = domain.Cell{X: 0, Z: -1}
)

func planLaboratory(f InteriorFrame) ([]InteriorPiece, bool) {
	// Bench (two rows), its interaction row, and the entrance row clear.
	if f.Depth < 4 {
		return nil, false
	}
	starts, ok := RowStarts(f.Width, researchBenchSize.X, 1, RowCapacity(f.Width, researchBenchSize.X, 1), RowCentred)
	if !ok {
		return nil, false
	}
	var out []InteriorPiece
	for i, u := range starts {
		// At North the (0,-1) interaction offset points toward the
		// entrance, one row in front of the bench.
		p := NewInteriorPiece(fmt.Sprintf("bench.%d", i+1), ResearchBenchDefinition, researchBenchSize, domain.North, domain.Cell{X: u, Z: f.Depth - 2})
		p.InteractionOffset = &researchBenchInteraction
		p.Row = "benches"
		out = append(out, p)
	}
	return out, true
}
