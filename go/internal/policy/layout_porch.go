package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// porchLength is the porch's long side in cells: two batteries end to end.
const porchLength int32 = 4

// PlannedPorch is a cold-map shelter's open lean-to child room: a one-cell
// strip on the exterior face of the wall opposite the parent's door, roofed
// (vanilla's Build Roof area, the BuildRoofAction) and holding the first
// batteries until the battery room stands. It has no walls and no door, so the
// heated room never opens onto it (Unfenced). The room's Region is the strip; a
// strip longer along x holds 1x2 batteries turned east, one longer along z
// batteries facing north.
const PlannedPorch PlannedRole = "porch"

// attachPorch is the porch for a shelter on interior in whose door faces rot:
// against the wall opposite the door, from the interior's near corner.
func attachPorch(in Rectangle, rot domain.Rotation) PlannedRoom {
	var strip Rectangle
	switch rot {
	case domain.North: // door on the north wall: the porch is outside the south wall
		strip = Rectangle{X: in.X, Z: in.Z - 2, Width: porchLength, Height: 1}
	case domain.South:
		strip = Rectangle{X: in.X, Z: in.Z + in.Height + 1, Width: porchLength, Height: 1}
	case domain.East:
		strip = Rectangle{X: in.X - 2, Z: in.Z, Width: 1, Height: porchLength}
	default:
		strip = Rectangle{X: in.X + in.Width + 1, Z: in.Z, Width: 1, Height: porchLength}
	}
	return PlannedRoom{Role: PlannedPorch, Region: RectRegion(strip), DoorRot: rot}
}

// PorchGround is the rectangle the porch occupies beyond its parent's ring;
// empty for any room but a porch.
func (r PlannedRoom) PorchGround() Rectangle {
	if r.Role != PlannedPorch {
		return Rectangle{}
	}
	return r.Region.Bounds()
}

// PorchSlots are the porch's battery footprints, nearest the interior's
// near corner first: 1x2 turned east across a strip along x, facing north
// along one along z.
func (r PlannedRoom) PorchSlots() []Rectangle {
	if r.Role != PlannedPorch {
		return nil
	}
	s := r.Region.Bounds()
	var out []Rectangle
	if s.Width > s.Height {
		for x := s.X; x+1 < s.X+s.Width; x += 2 {
			out = append(out, Rectangle{X: x, Z: s.Z, Width: 2, Height: 1})
		}
		return out
	}
	for z := s.Z; z+1 < s.Z+s.Height; z += 2 {
		out = append(out, Rectangle{X: s.X, Z: z, Width: 1, Height: 2})
	}
	return out
}

// PorchRoofCells are the cells to designate in the Build Roof area for every
// porch in the plan, in plan order.
func PorchRoofCells(plan LayoutPlan) []domain.Cell {
	var out []domain.Cell
	for _, r := range plan.roomsOf(PlannedPorch) {
		out = append(out, RectangleCells(r.Region.Bounds())...)
	}
	return out
}

// porchSites are the battery sites on the plan's porches, in plan order.
func porchSites(plan LayoutPlan) []PlannedPowerSite {
	var out []PlannedPowerSite
	for _, r := range plan.roomsOf(PlannedPorch) {
		for _, s := range r.PorchSlots() {
			rot := domain.East
			if s.Height == 2 {
				rot = domain.North
			}
			out = append(out, PlannedPowerSite{Cell: domain.Cell{X: s.X, Z: s.Z}, Rotation: rot, Area: s})
		}
	}
	return out
}
