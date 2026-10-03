package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The workshop template (#803): one centred row of benches on the back
// wall, each facing the entrance with its interaction cell on the open
// floor in front of it. A tool cabinet stands on end in every one-cell gap
// between two slots, so each cabinet feeds both neighbours and no bench
// links more than the two a ToolCabinet allows (CompProperties_Facility
// maxSimultaneous 2; maxDistance 8 is never the limit here). A small input
// shelf sits beside each interaction cell.
//
// The benches are the piece being placed when it is a workshop bench (the
// 3x1 BenchBase benches or the 5x2 FabricationBench, #820), else the
// stonecutter; BenchRow widens every slot to the widest bench standing in
// the room. Shelves score 1 each toward Storeroom against 27 per bench
// toward Workshop, and there is one shelf per bench, so the plan never
// flips the room's role.

const (
	workshopBenchDef   = "TableStonecutter"
	workshopCabinetDef = "ToolCabinet"
	workshopShelfDef   = "ShelfSmall"
)

func init() {
	RegisterInteriorTemplate(RoomRoleWorkshop, InteriorTemplate{Name: "workshop", Plan: planWorkshop})
}

func planWorkshop(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	def, ok := BenchRowDef(f, piece, RoomRoleWorkshop, workshopBenchDef)
	cabinet, cok := f.Shapes.Get(workshopCabinetDef)
	shelf, sok := f.Shapes.Get(workshopShelfDef)
	if !ok || !cok || !sok {
		return nil, false
	}
	benches, starts, pitch, ok := BenchRow(f, def, 1, 0, func(i int) string { return fmt.Sprintf("bench.%d", i) })
	if !ok {
		return nil, false
	}
	var out []InteriorPiece
	for i, b := range benches {
		b.Row = "benches"
		out = append(out, b)
		if i < len(starts)-1 {
			// On end (East: 1 wide, 2 deep) in the gap against the back wall.
			c := NewInteriorPiece(fmt.Sprintf("cabinet.%d", i), workshopCabinetDef, cabinet.Size, domain.East, domain.Cell{X: starts[i] + pitch, Z: f.Depth - cabinet.Size.X})
			c.Row = "cabinets"
			out = append(out, c)
		}
		// The shelf keeps its bench's spacing but sits off-centre beside the
		// interaction cell, so it forms no row of its own.
		ic, _ := b.Interaction()
		out = append(out, NewInteriorPiece(fmt.Sprintf("shelf.%d", i), workshopShelfDef, shelf.Size, domain.North, domain.Cell{X: ic.X + 1, Z: ic.Z}))
	}
	return out, true
}
