package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The training range: a planned Outdoor room with no ring at all (no fence,
// gate, door, roof or floor), the open-air firing range of RangeLayout. Its
// Interior is the two rows and the lane between them; its Facing says which end
// the dummies stand at. It is sited near the colony (rangeGap, not the
// outskirts' outskirtsGap) with its dummy end pointing away from the core. It grows with the colony: the plan holds
// as many stands as RoomDemand.Ranges asks for and never fewer.

// PlannedTrainingRange is the training range's plan role.
const PlannedTrainingRange PlannedRole = "training_range"

const (
	// RangeMinStands and RangeMaxStands bound the stand count.
	RangeMinStands = 2
	RangeMaxStands = 8
)

// RangeStandsFor is the stand count for a colony of capable adults: one per two,
// within RangeMinStands and RangeMaxStands.
func RangeStandsFor(capable int) int {
	return min(max((capable+1)/2, RangeMinStands), RangeMaxStands)
}

// Unfenced reports whether r has no ring: a training range, sparring ring, graveyard or yard
// stands in open air with nothing built round it, so it owes no wall, fence,
// gate, roof or floor and has no Door. Only the animal pen keeps a fence.
func (r PlannedRoom) Unfenced() bool {
	switch r.Role {
	case PlannedTrainingRange, PlannedSparringRing, PlannedGraveyard, PlannedWasteYard, PlannedYard, PlannedAirlock, PlannedPorch:
		return true
	}
	return false
}

// RangeStandCount is the stands a planned range holds: the length of its rows.
func RangeStandCount(r PlannedRoom) int32 {
	switch r.Facing {
	case domain.North, domain.South:
		return r.Region.Bounds().Width
	case domain.East, domain.West:
		return r.Region.Bounds().Height
	}
	return 0
}

// rangeBlock is the footprint an unfenced range blocks: its interior with a
// one-cell margin. A stray practice shot cannot hit a pawn (see the drill), so
// nothing behind the dummies is kept free.
func rangeBlock(r PlannedRoom) Rectangle { return pad(r.Region.Bounds(), 1) }

// rangeBlockSize is the footprint of a range of stands stands in the given
// orientation.
func rangeBlockSize(stands int32, horizontal bool) (w, h int32) {
	w, h = stands+2, RangeDistance+1+2
	if !horizontal {
		w, h = h, w
	}
	return w, h
}

// rangeFromBlock is the planned range whose blocked footprint is block.
func rangeFromBlock(block Rectangle, facing domain.Rotation, stands int32) PlannedRoom {
	in := Rectangle{X: block.X + 1, Z: block.Z + 1, Width: stands, Height: RangeDistance + 1}
	if facing == domain.East || facing == domain.West {
		in.Width, in.Height = RangeDistance+1, stands
	}
	return PlannedRoom{Role: PlannedTrainingRange, Region: RectRegion(in), Outdoor: true, Facing: facing}
}

// RangeRooms are the plan's training ranges.
func (p LayoutPlan) RangeRooms() []PlannedRoom { return p.roomsOf(PlannedTrainingRange) }

// RangeTemplate is the furniture template of a planned range: RangeLayout, one
// WantedPiece per building.
func RangeTemplate(room PlannedRoom) []WantedPiece {
	layout := RangeLayout(room)
	out := make([]WantedPiece, 0, len(layout))
	for _, p := range layout {
		out = append(out, WantedPiece{DefName: RangeDefNames[p.Kind], Minimum: p.Cell, Maximum: p.Cell, Slot: fmt.Sprintf("%s-%d", p.Kind, p.Column), Size: domain.Cell{X: 1, Z: 1}, Rot: domain.North})
	}
	return out
}

// rangeFacing is the direction the dummies point for a site on side of the core
// extent ext, rows running along X when horizontal: the side itself when it
// lies along the dummy axis, else the end of that axis farther from the core.
func rangeFacing(side domain.Rotation, horizontal bool, block, ext Rectangle) domain.Rotation {
	if horizontal {
		if side == domain.North || side == domain.South {
			return side
		}
		if block.Z+block.Height/2 >= ext.Z+ext.Height/2 {
			return domain.North
		}
		return domain.South
	}
	if side == domain.East || side == domain.West {
		return side
	}
	if block.X+block.Width/2 >= ext.X+ext.Width/2 {
		return domain.East
	}
	return domain.West
}
