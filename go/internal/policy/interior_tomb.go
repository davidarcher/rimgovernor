package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The tomb template (#831): sarcophagi side by side in one row, heads
// against the back wall, feet toward the entrance, with the open floor in
// front as the aisle the burial haul walks. A Sarcophagus is a 1x2
// Building_Grave (Buildings_Misc.xml) with no interaction cell; haulers
// inter a corpse by touching it, so the row needs no gaps, only the aisle.
// MaintainWaste's burial haul already fills any empty Building_Grave.

func init() {
	RegisterInteriorTemplate(RoomRoleTomb, InteriorTemplate{Name: "tomb", Plan: planTomb})
}

// SarcophagusDefinition is Core's sarcophagus.
const SarcophagusDefinition = "Sarcophagus"

func planTomb(f InteriorFrame, _ InteriorPieceDef) ([]InteriorPiece, bool) {
	// Two cells of sarcophagus and at least the aisle in front.
	if f.Depth < 3 {
		return nil, false
	}
	starts, ok := RowStarts(f.Width, 1, 0, f.Width, RowCentred)
	if !ok {
		return nil, false
	}
	out := make([]InteriorPiece, 0, len(starts))
	for i, u := range starts {
		p := NewInteriorPiece(fmt.Sprintf("sarcophagus.%d", i+1), SarcophagusDefinition, domain.Cell{X: 1, Z: 2}, domain.South, domain.Cell{X: u, Z: f.Depth - 2})
		p.Row = "sarcophagi"
		out = append(out, p)
	}
	return out, true
}
