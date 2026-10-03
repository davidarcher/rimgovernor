package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The tomb template (#831, #832): double-sided like the battery room
// (BatterySlots), a 1-cell aisle straight in from the door with
// sarcophagi packed along both sides, heads against the side walls and
// feet on the aisle. A Sarcophagus is a 1x2 Building_Grave
// (Buildings_Misc.xml) with no interaction cell and no chain reaction, so
// the rows need no spacers; haulers inter a corpse from the aisle.
// Vanilla haulers fill any empty grave with a colonist corpse, and the
// Tomb role carries no mood effect, so there is no furnishing tier: the
// payoff is KnowBuriedInSarcophagus on every colonist.

func init() {
	RegisterInteriorTemplate(RoomRoleTomb, InteriorTemplate{Name: "tomb", Plan: planTomb})
}

func planTomb(f InteriorFrame, _ InteriorPieceDef) ([]InteriorPiece, bool) {
	sarcophagus, ok := f.Shapes.Get(f.Shapes.Furniture.Sarcophagus)
	if !ok {
		return nil, false
	}
	aisle := f.Entrance
	// Turned to face the aisle a sarcophagus is Size.Z cells wide and Size.X
	// deep.
	size, reach := sarcophagus.Size, sarcophagus.Size.Z
	var out []InteriorPiece
	for _, v := range AisleRows(f.Depth, size.X) {
		if v+size.X > f.Depth {
			continue
		}
		if aisle >= reach {
			p := NewInteriorPiece(fmt.Sprintf("sarcophagus.w%d", v+1), sarcophagus.Def, size, domain.West, domain.Cell{X: aisle - reach, Z: v})
			p.Row = "sarcophagi.west"
			out = append(out, p)
		}
		if aisle+1+reach <= f.Width {
			p := NewInteriorPiece(fmt.Sprintf("sarcophagus.e%d", v+1), sarcophagus.Def, size, domain.East, domain.Cell{X: aisle + 1, Z: v})
			p.Row = "sarcophagi.east"
			out = append(out, p)
		}
	}
	return out, len(out) > 0
}
