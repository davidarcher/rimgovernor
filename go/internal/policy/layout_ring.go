package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The sparring ring: a planned Outdoor room with no ring of its own (no fence,
// gate, door, roof or floor), a clear square beside the training range where
// colonists spar. Its shape never changes: the interior is always RingSide
// cells square, the centre is left free for fighting, and marker slots sit at
// fixed positions on the inner edge, so slot n is always the same cell. It
// grows only by markers: the plan holds as many as RoomDemand.Rings asks for
// (PlannedRoom.Markers) and never fewer, each new one taking the next free
// slot; nothing moves or is rebuilt.

// PlannedSparringRing is the sparring ring's plan role.
const PlannedSparringRing PlannedRole = "sparring_ring"

const (
	// RingMinMarkers and RingMaxMarkers bound the marker count.
	RingMinMarkers = 4
	RingMaxMarkers = 12
	// RingSide is the ring interior's side in cells.
	RingSide int32 = 9
)

// RingMarkerDef is the mod's marker building.
const RingMarkerDef = "RimGovernor_SparringMarker"

// RingMarkersFor is the marker count for a colony of capable adults: one each,
// within RingMinMarkers and RingMaxMarkers.
func RingMarkersFor(capable int) int {
	return min(max(capable, RingMinMarkers), RingMaxMarkers)
}

// RingRooms are the plan's sparring rings.
func (p LayoutPlan) RingRooms() []PlannedRoom { return p.roomsOf(PlannedSparringRing) }

// ringBlock is the footprint a ring blocks: its interior with a one-cell margin.
func ringBlock(r PlannedRoom) Rectangle { return pad(r.Region.Bounds(), 1) }

// ringFromBlock is the planned ring whose blocked footprint is block.
func ringFromBlock(block Rectangle, markers int) PlannedRoom {
	in := Rectangle{X: block.X + 1, Z: block.Z + 1, Width: RingSide, Height: RingSide}
	return PlannedRoom{Role: PlannedSparringRing, Region: RectRegion(in), Outdoor: true, Markers: markers}
}

// RingSlot is the cell of marker slot n on the inner edge of a ring whose
// interior is in. Slots go round the four sides (south, north, west, east), one
// per side at a time, each side's next slot two cells further along it; the
// corners and the centre stay free.
func RingSlot(in Rectangle, n int) domain.Cell {
	along := 2 + 2*int32(n/4)
	switch n % 4 {
	case 0:
		return domain.Cell{X: in.X + along, Z: in.Z}
	case 1:
		return domain.Cell{X: in.X + in.Width - 1 - along, Z: in.Z + in.Height - 1}
	case 2:
		return domain.Cell{X: in.X, Z: in.Z + in.Height - 1 - along}
	}
	return domain.Cell{X: in.X + in.Width - 1, Z: in.Z + along}
}

// RingTemplate is the furniture template of a planned ring: one marker in each
// of its first Markers slots.
func RingTemplate(room PlannedRoom) []WantedPiece {
	out := make([]WantedPiece, 0, room.Markers)
	for n := range min(room.Markers, RingMaxMarkers) {
		c := RingSlot(room.Region.Bounds(), n)
		out = append(out, WantedPiece{DefName: RingMarkerDef, Minimum: c, Maximum: c, Slot: fmt.Sprintf("marker-%d", n), Size: domain.Cell{X: 1, Z: 1}, Rot: domain.North})
	}
	return out
}
