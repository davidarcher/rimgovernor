package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The paddock marker: the wall's yard is the animal pen,
// claimed by one PenMarker. Pen membership comes from the native enclosed-pen
// lookup, so a marker standing is not an animal contained.

const (
	// PenFenceDefinition and PenGateDefinition are the defs of an outdoor
	// room's ring (the materials yard).
	PenFenceDefinition = "Fence"
	PenGateDefinition  = "FenceGate"
	// PenMarkerDefinition is the paddock's one piece.
	PenMarkerDefinition = "PenMarker"
	// penMarkerSlot is the marker's template slot.
	penMarkerSlot = "pen.marker"
)

// PaddockStep is the one PenMarker the wall's yard is claimed by.
type PaddockStep struct {
	// Marker says a marker already stands inside the ring.
	Marker bool
	// Candidates are the free anchor cells a marker may go on, best first:
	// the unreserved, unbuilt cells inside the ring, nearest its centre.
	Candidates []domain.Cell
}

// NextPaddockStep reads the yard inside the plan's wall: the cells strictly
// inside the ring that no reservation covers and nothing stands on. A
// standing marker inside the ring ends the step, so a repeat pass plans
// nothing. ok is false for a plan without a ring. marker is the PenMarker's
// native shape; a zero size is read as one cell.
func NextPaddockStep(plan LayoutPlan, ground GroundCensus, built []CurrentBuilding, marker InteriorPieceDef) (PaddockStep, bool) {
	wi, ok := planInterior(plan, 0)
	if !ok {
		return PaddockStep{}, false
	}
	var step PaddockStep
	taken := map[domain.Cell]bool{}
	for _, m := range []map[domain.Cell]bool{ground.walls, ground.doors, ground.fences, ground.gates, ground.flaps} {
		for c := range m {
			taken[c] = true
		}
	}
	for _, b := range built {
		for _, c := range b.Cells {
			taken[c] = true
			if i, inside := wi.at(c); inside && b.Building.Definition() == PenMarkerDefinition && wi.closed[i] {
				step.Marker = true
			}
		}
	}
	if step.Marker {
		return step, true
	}
	for _, r := range plan.Reservations {
		if r.Kind == ReserveCoverClear {
			continue
		}
		for _, c := range rectCells(r.Area) {
			taken[c] = true
		}
	}
	size := marker.Size
	if size.X < 1 || size.Z < 1 {
		size = domain.Cell{X: 1, Z: 1}
	}
	free := func(c domain.Cell) bool {
		if i, inside := wi.at(c); !inside || !wi.in[i] {
			return false
		}
		return !taken[c]
	}
	for _, c := range wi.cells() {
		fits := true
		for _, m := range rectCells(Rectangle{X: c.X, Z: c.Z, Width: size.X, Height: size.Z}) {
			fits = fits && free(m)
		}
		if fits {
			step.Candidates = append(step.Candidates, c)
		}
	}
	cx, cz := wi.box.X+wi.box.Width/2, wi.box.Z+wi.box.Height/2
	dist := func(c domain.Cell) int32 { return (c.X-cx)*(c.X-cx) + (c.Z-cz)*(c.Z-cz) }
	slices.SortStableFunc(step.Candidates, func(a, b domain.Cell) int { return int(dist(a) - dist(b)) })
	return step, true
}

// PaddockMarkerPiece is the marker's wanted piece anchored at anchor.
func PaddockMarkerPiece(anchor domain.Cell, marker InteriorPieceDef) WantedPiece {
	size := marker.Size
	if size.X < 1 || size.Z < 1 {
		size = domain.Cell{X: 1, Z: 1}
	}
	return WantedPiece{DefName: PenMarkerDefinition, Minimum: anchor, Maximum: domain.Cell{X: anchor.X + size.X - 1, Z: anchor.Z + size.Z - 1}, Slot: penMarkerSlot, Size: size, Rot: domain.North}
}
