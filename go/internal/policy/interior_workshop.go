package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The workshop template (#803): one centred row of benches on the back
// wall, each facing the entrance with its interaction cell on the open
// floor in front of it. A tool cabinet stands on end in every one-cell gap
// between two benches, so each cabinet feeds both neighbours and no bench
// links more than the two a ToolCabinet allows (CompProperties_Facility
// maxSimultaneous 2; maxDistance 8 is never the limit here). A small input
// shelf sits beside each interaction cell, between it and the cabinet.
//
// A bench slot is the BenchBase footprint: 3x1 with the interaction cell
// one cell in front, which every vanilla workbench the colony builds shares
// (stonecutter, smithies, tailoring, machining, smelter, art, butcher,
// stoves, brewery, drug lab); a 1x1 CraftingSpot with the same offset fits
// the slot's centre. Shelves score 1 each toward Storeroom against 27 per
// bench toward Workshop, and there is one shelf per bench, so the plan
// never flips the room's role.

const (
	workshopBenchDef   = "TableStonecutter"
	workshopCabinetDef = "ToolCabinet"
	workshopShelfDef   = "ShelfSmall"
)

func init() {
	RegisterInteriorTemplate(RoomRoleWorkshop, InteriorTemplate{Name: "workshop", Plan: planWorkshop})
}

func planWorkshop(f InteriorFrame) ([]InteriorPiece, bool) {
	// Benches on the back row, interaction cells and shelves on the row in
	// front; the row before that (at least the entrance row) stays open.
	if f.Depth < 3 {
		return nil, false
	}
	const span, gap = 3, 1
	n := RowCapacity(f.Width, span, gap)
	starts, ok := RowStarts(f.Width, span, gap, n, RowCentred)
	if !ok {
		return nil, false
	}
	back, front := f.Depth-1, f.Depth-2
	var out []InteriorPiece
	for i, u := range starts {
		b := NewInteriorPiece(fmt.Sprintf("bench.%d", i), workshopBenchDef, domain.Cell{X: 3, Z: 1}, domain.North, domain.Cell{X: u, Z: back})
		b.InteractionOffset, b.Row = &domain.Cell{X: 0, Z: -1}, "benches"
		// The shelf keeps its bench's spacing but sits off-centre beside the
		// interaction cell, so it forms no row of its own.
		s := NewInteriorPiece(fmt.Sprintf("shelf.%d", i), workshopShelfDef, domain.Cell{X: 1, Z: 1}, domain.North, domain.Cell{X: u + 2, Z: front})
		out = append(out, b)
		if i < len(starts)-1 {
			// On end (East: 1 wide, 2 deep) in the gap, beside both benches.
			c := NewInteriorPiece(fmt.Sprintf("cabinet.%d", i), workshopCabinetDef, domain.Cell{X: 2, Z: 1}, domain.East, domain.Cell{X: u + span, Z: front})
			c.Row = "cabinets"
			out = append(out, c)
		}
		out = append(out, s)
	}
	return out, true
}
