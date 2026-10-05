package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The animal pen as a planned room (#2120, epic #2101). Each ReservePen
// reservation is an outdoor room (herdRoom): its outline is the ring of
// fences with a gate where the plan puts the door, its interior open ground
// that is never roofed or floored. The pen's one piece is the PenMarker; pen
// membership still comes from the native enclosed-pen lookup, so a ring and a
// marker standing is the pen built, not an animal contained. The planner sites
// the pen: the build side never searches another site.

const (
	// PenFenceDefinition and PenGateDefinition are the defs of a pen's ring.
	PenFenceDefinition = "Fence"
	PenGateDefinition  = "FenceGate"
	// PenMarkerDefinition is the pen's one piece.
	PenMarkerDefinition = "PenMarker"
	// penMarkerSlot is the marker's template slot, the pen's interior centre.
	penMarkerSlot = "pen.marker"
)

// PenStep is the pen the build side reconciles next.
type PenStep struct {
	Room PlannedRoom
	// Template is the room's wanted pieces: the standing marker where it
	// stands, else the marker in its slot.
	Template []WantedPiece
	// Ring and Marker say the ring matches the plan and a marker stands inside.
	Ring, Marker bool
}

// Stands reports the pen built: its ring matches and its marker stands.
func (s PenStep) Stands() bool { return s.Ring && s.Marker }

// NextPenStep is the first planned pen that is not built, else the first pen
// (standing); false when the plan holds no pen reservation. marker is the
// PenMarker's native shape; a zero size is read as one cell.
func NextPenStep(plan LayoutPlan, ground GroundCensus, built []CurrentBuilding, marker InteriorPieceDef) (PenStep, bool) {
	var first PenStep
	for i, room := range plan.HerdRooms(PlannedPen) {
		step := penStep(plan, room, ground, built, marker)
		if i == 0 {
			first = step
		}
		if !step.Stands() {
			return step, true
		}
	}
	return first, first.Room.Interior != (Rectangle{})
}

func penStep(plan LayoutPlan, room PlannedRoom, ground GroundCensus, built []CurrentBuilding, marker InteriorPieceDef) PenStep {
	step := PenStep{Room: room, Ring: plan.GroundMatches(room, ground)}
	for _, b := range built {
		if b.Building.Definition() != PenMarkerDefinition || len(b.Cells) == 0 || !rectInside(room.Interior, cellsRectangle(b.Cells)) {
			continue
		}
		r := cellsRectangle(b.Cells)
		step.Marker = true
		step.Template = []WantedPiece{{DefName: PenMarkerDefinition, Minimum: domain.Cell{X: r.X, Z: r.Z}, Maximum: domain.Cell{X: r.X + r.Width - 1, Z: r.Z + r.Height - 1}}}
		return step
	}
	size := marker.Size
	if size.X < 1 || size.Z < 1 {
		size = domain.Cell{X: 1, Z: 1}
	}
	in := room.Interior
	at := domain.Cell{X: in.X + in.Width/2, Z: in.Z + in.Height/2}
	step.Template = []WantedPiece{{DefName: PenMarkerDefinition, Minimum: at, Maximum: domain.Cell{X: at.X + size.X - 1, Z: at.Z + size.Z - 1}, Slot: penMarkerSlot, Size: size, Rot: domain.North}}
	return step
}
