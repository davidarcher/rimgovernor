package policy

import (
	"fmt"
	"slices"

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
func ringBlock(r PlannedRoom) Rectangle { return pad(r.Interior, 1) }

// ringFromBlock is the planned ring whose blocked footprint is block.
func ringFromBlock(block Rectangle, markers int) PlannedRoom {
	in := Rectangle{X: block.X + 1, Z: block.Z + 1, Width: RingSide, Height: RingSide}
	return PlannedRoom{Role: PlannedSparringRing, Interior: in, Outdoor: true, Markers: markers}
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
		c := RingSlot(room.Interior, n)
		out = append(out, WantedPiece{DefName: RingMarkerDef, Minimum: c, Maximum: c, Slot: fmt.Sprintf("marker-%d", n), Size: domain.Cell{X: 1, Z: 1}, Rot: domain.North})
	}
	return out
}

// RingsOwed is the markers the rings demand asks for that the plan lacks.
func RingsOwed(plan LayoutPlan, demand RoomDemand) int {
	have := 0
	if rooms := plan.RingRooms(); len(rooms) > 0 {
		have = rooms[0].Markers
	}
	return max(min(demand.Rings, RingMaxMarkers)-have, 0)
}

// growRings adds the markers the rings demand asks for and the plan lacks: a
// first ring is sited (siteRing), a standing one takes more markers
// (widenRing). No room moves. It reports whether the plan changed; a ring that
// fits nowhere is left out.
func growRings(plan LayoutPlan, demand RoomDemand) (LayoutPlan, bool) {
	owed := RingsOwed(plan, demand)
	if owed == 0 {
		return plan, false
	}
	rooms := plan.RingRooms()
	if len(rooms) == 0 {
		room, ok := siteRing(plan, min(max(demand.Rings, RingMinMarkers), RingMaxMarkers))
		if !ok {
			return plan, false
		}
		plan.Rooms = append(slices.Clone(plan.Rooms), room)
		return plan, true
	}
	grown := widenRing(rooms[0], owed)
	plan.Rooms = slices.Clone(plan.Rooms)
	for i, r := range plan.Rooms {
		if r.Same(rooms[0]) {
			plan.Rooms[i] = grown
		}
	}
	return plan, true
}

// widenRing is room with up to extra more markers, in the next free slots.
func widenRing(room PlannedRoom, extra int) PlannedRoom {
	room.Markers = min(room.Markers+extra, RingMaxMarkers)
	return room
}

// siteRing sites a ring of markers markers: among the sites the outskirts
// search finds with rangeGap, the one nearest the training range when the plan
// has one (beside it), else nearest the core. False when nothing fits.
func siteRing(plan LayoutPlan, markers int) (PlannedRoom, bool) {
	anchor, ok := plan.CoreBounds()
	if !ok {
		return PlannedRoom{}, false
	}
	if ranges := plan.RangeRooms(); len(ranges) > 0 {
		anchor = rangeBlock(ranges[0])
	}
	cands, _, _, _ := outskirtsCandidatesAt(plan, RingSide+2, RingSide+2, rangeGap)
	var best Rectangle
	found, bestDist := false, int32(0)
	for _, side := range outskirtsSides {
		block, ok := cands[side]
		if !ok {
			continue
		}
		dx := (block.X + block.Width/2) - (anchor.X + anchor.Width/2)
		dz := (block.Z + block.Height/2) - (anchor.Z + anchor.Height/2)
		if d := dx*dx + dz*dz; !found || d < bestDist {
			best, found, bestDist = block, true, d
		}
	}
	if !found {
		return PlannedRoom{}, false
	}
	return ringFromBlock(best, markers), true
}
