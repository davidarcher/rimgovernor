package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The training range: a walled room in the outskirts, sited beside the
// cluster like a further graveyard. Its 5 by 12 interior (RangeWidth by
// RangeLaneLength) never fits a core room (coreMaxDepth is 7), and a lane is a
// firing line, so it stands clear of every living room. It is demand-grown
// (RoomDemand.Ranges); nothing is reserved ahead of the need. The template is
// RangeLayout's, reconciled through the room reconciler like the tomb's pieces.

// PlannedTrainingRange is the training range's plan role.
const PlannedTrainingRange PlannedRole = "training_range"

// RangeRooms are the plan's training ranges.
func (p LayoutPlan) RangeRooms() []PlannedRoom { return p.roomsOf(PlannedTrainingRange) }

// RangesOwed is the ranges demand asks for that plan lacks.
func RangesOwed(plan LayoutPlan, demand RoomDemand) int {
	return max(demand.Ranges-len(plan.RangeRooms()), 0)
}

// RangeTemplate is the furniture template of a planned range: RangeLayout from
// the interior's south-west cell, one WantedPiece per building.
func RangeTemplate(room PlannedRoom) []WantedPiece {
	layout := RangeLayout(domain.Cell{X: room.Interior.X, Z: room.Interior.Z})
	out := make([]WantedPiece, 0, len(layout))
	for i, p := range layout {
		out = append(out, WantedPiece{DefName: RangeDefNames[p.Kind], Minimum: p.Cell, Maximum: p.Cell, Slot: fmt.Sprintf("%s-%d", p.Kind, i), Size: domain.Cell{X: 1, Z: 1}, Rot: domain.North})
	}
	return out
}

// growRanges adds the ranges demand asks for and plan lacks. A range is a
// walled room sited like the outskirts cluster (outskirtsCandidates:
// outskirtsGap clear of every room, off the growth lines, walkable from the
// core), the nearest site to the cluster. Its door is on the west wall at the
// middle of the lanes' length, opening onto lane 0 between stand and dummy. No room
// moves. It reports whether a range was added; one that fits nowhere is left
// out.
func growRanges(plan LayoutPlan, demand RoomDemand) (LayoutPlan, bool) {
	owed := RangesOwed(plan, demand)
	added := false
	for ; owed > 0; owed-- {
		best, _, _, _ := outskirtsCandidates(plan, RangeWidth+2, RangeLaneLength+2)
		area, ok := nearestSite(best, plan)
		if !ok {
			break
		}
		in := Rectangle{X: area.X + 1, Z: area.Z + 1, Width: RangeWidth, Height: RangeLaneLength}
		plan.Rooms = append(slices.Clone(plan.Rooms), PlannedRoom{Role: PlannedTrainingRange, Interior: in, Door: domain.Cell{X: area.X, Z: in.Z + in.Height/2}, DoorRot: domain.West})
		added = true
	}
	return plan, added
}
