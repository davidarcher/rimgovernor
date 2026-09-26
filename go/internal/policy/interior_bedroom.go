package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The bedroom template: one bed on the centre line, its head against the
// wall facing the entrance. End tables, dresser and lamp are #802.

func init() {
	RegisterInteriorTemplate(RoomRoleBedroom, InteriorTemplate{Name: "bedroom", Plan: planBedroom})
}

func planBedroom(f InteriorFrame) ([]InteriorPiece, bool) {
	// A bed's head is its far cell in the facing direction, so a North bed
	// against the back wall has its head on it. The row inside the
	// entrance stays floor.
	if f.Depth < 3 {
		return nil, false
	}
	bed := NewInteriorPiece("bed", "Bed", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: CentreStart(f.Width, 1), Z: f.Depth - 2})
	bed.Centred = true
	return []InteriorPiece{bed}, true
}
