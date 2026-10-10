package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The training range: a planned Outdoor room with no ring at all (no fence,
// gate, door, roof or floor), the open-air firing range of RangeLayout. Its
// Interior is the two rows and the lane between them; its Facing says which end
// the dummies stand at. It is sited near the colony (rangeGap, not the
// outskirts' outskirtsGap) with its dummy end pointing away from the core, and
// RangeBackdrop cells behind the dummies are kept free of buildings by the
// room's blocked footprint (roomWalls). It grows with the colony: the plan holds
// as many stands as RoomDemand.Ranges asks for and never fewer.

// PlannedTrainingRange is the training range's plan role.
const PlannedTrainingRange PlannedRole = "training_range"

const (
	// RangeMinStands and RangeMaxStands bound the stand count.
	RangeMinStands = 2
	RangeMaxStands = 8
	// RangeBackdrop is the cells behind the dummies kept free of buildings.
	RangeBackdrop int32 = 4
	// rangeGap is the clear ground, in cells, between the range and every other
	// room: less than outskirtsGap, since the range is no dump to keep clear of
	// living rooms, only a firing line to keep clear of walls.
	rangeGap int32 = 2
)

// RangeStandsFor is the stand count for a colony of capable adults: one per two,
// within RangeMinStands and RangeMaxStands.
func RangeStandsFor(capable int) int {
	return min(max((capable+1)/2, RangeMinStands), RangeMaxStands)
}

// Unfenced reports whether r has no ring: the training range stands in open air
// with nothing built round it, so it owes no wall, fence, door, roof or floor.
func (r PlannedRoom) Unfenced() bool { return r.Role == PlannedTrainingRange }

// rangeHorizontal is true when the range's rows run along X (dummies north or
// south of the stands).
func (r PlannedRoom) rangeHorizontal() bool {
	return r.Facing == domain.North || r.Facing == domain.South
}

// RangeStandCount is the stands a planned range holds: the length of its rows.
func RangeStandCount(r PlannedRoom) int32 {
	switch r.Facing {
	case domain.North, domain.South:
		return r.Interior.Width
	case domain.East, domain.West:
		return r.Interior.Height
	}
	return 0
}

// rangeBlock is the footprint an unfenced range blocks: its interior with a
// one-cell margin and RangeBackdrop cells more behind the dummies.
func rangeBlock(r PlannedRoom) Rectangle {
	b := pad(r.Interior, 1)
	switch r.Facing {
	case domain.North:
		b.Height += RangeBackdrop
	case domain.South:
		b.Z -= RangeBackdrop
		b.Height += RangeBackdrop
	case domain.East:
		b.Width += RangeBackdrop
	case domain.West:
		b.X -= RangeBackdrop
		b.Width += RangeBackdrop
	}
	return b
}

// rangeBlockSize is the footprint of a range of stands stands in the given
// orientation.
func rangeBlockSize(stands int32, horizontal bool) (w, h int32) {
	w, h = stands+2, RangeDistance+1+2+RangeBackdrop
	if !horizontal {
		w, h = h, w
	}
	return w, h
}

// rangeFromBlock is the planned range whose blocked footprint is block.
func rangeFromBlock(block Rectangle, facing domain.Rotation, stands int32) PlannedRoom {
	in := Rectangle{X: block.X + 1, Z: block.Z + 1, Width: stands, Height: RangeDistance + 1}
	switch facing {
	case domain.South:
		in.Z += RangeBackdrop
	case domain.East, domain.West:
		in.Width, in.Height = RangeDistance+1, stands
		if facing == domain.West {
			in.X += RangeBackdrop
		}
	}
	return PlannedRoom{Role: PlannedTrainingRange, Interior: in, Outdoor: true, Facing: facing}
}

// RangeRooms are the plan's training ranges.
func (p LayoutPlan) RangeRooms() []PlannedRoom { return p.roomsOf(PlannedTrainingRange) }

// RangesOwed is the stands the ranges demand asks for that the plan lacks.
func RangesOwed(plan LayoutPlan, demand RoomDemand) int {
	have := 0
	if rooms := plan.RangeRooms(); len(rooms) > 0 {
		have = int(RangeStandCount(rooms[0]))
	}
	return max(min(demand.Ranges, RangeMaxStands)-have, 0)
}

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

// growRanges adds the stands the ranges demand asks for and the plan lacks:
// a first range is sited (siteRange), a standing one is widened along its rows
// (widenRange). No room moves. It reports whether the plan changed; a range that
// fits nowhere is left out.
func growRanges(plan LayoutPlan, demand RoomDemand) (LayoutPlan, bool) {
	owed := RangesOwed(plan, demand)
	if owed == 0 {
		return plan, false
	}
	rooms := plan.RangeRooms()
	if len(rooms) == 0 {
		want := int32(min(demand.Ranges, RangeMaxStands))
		room, ok := siteRange(plan, want)
		if !ok && want > RangeMinStands {
			room, ok = siteRange(plan, RangeMinStands)
		}
		if !ok {
			return plan, false
		}
		plan.Rooms = append(slices.Clone(plan.Rooms), room)
		return plan, true
	}
	grown, ok := widenRange(plan, rooms[0], int32(owed))
	if !ok {
		return plan, false
	}
	plan.Rooms = slices.Clone(plan.Rooms)
	for i, r := range plan.Rooms {
		if r.Same(rooms[0]) {
			plan.Rooms[i] = grown
		}
	}
	return plan, true
}

// siteRange sites a range of stands stands: the nearest valid site to the core
// among those the outskirts search finds with rangeGap, one orientation of the
// rows at a time. A site whose dummy end points away from the core (the side it
// lies on) beats one off to the side of the core; a site never faces the core.
// False when nothing fits.
func siteRange(plan LayoutPlan, stands int32) (PlannedRoom, bool) {
	ext, ok := plan.CoreBounds()
	if !ok {
		return PlannedRoom{}, false
	}
	var best PlannedRoom
	found, bestRank, bestDist := false, 0, int32(0)
	for _, horizontal := range []bool{true, false} {
		w, h := rangeBlockSize(stands, horizontal)
		cands, _, _, _ := outskirtsCandidatesAt(plan, w, h, rangeGap)
		for _, side := range outskirtsSides {
			block, ok := cands[side]
			if !ok {
				continue
			}
			facing := rangeFacing(side, horizontal, block, ext)
			rank := 1
			if facing == side {
				rank = 0
			}
			dx := (block.X + block.Width/2) - (ext.X + ext.Width/2)
			dz := (block.Z + block.Height/2) - (ext.Z + ext.Height/2)
			if d := dx*dx + dz*dz; !found || rank < bestRank || rank == bestRank && d < bestDist {
				best, found, bestRank, bestDist = rangeFromBlock(block, facing, stands), true, rank, d
			}
		}
	}
	return best, found
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

// widenRange adds up to extra stands to room along its rows, as many as the
// free ground beside it allows (the + end first, then the - end). Standing
// pieces keep their cells. False when no stand fits.
func widenRange(plan LayoutPlan, room PlannedRoom, extra int32) (PlannedRoom, bool) {
	u := newUtilityGrid(plan)
	if u.w == 0 || u.h == 0 {
		return PlannedRoom{}, false
	}
	block := rangeBlock(room)
	horizontal := room.rangeHorizontal()
	for k := min(extra, int32(RangeMaxStands)-RangeStandCount(room)); k > 0; k-- {
		for _, plus := range []bool{true, false} {
			strip, grown := block, room
			switch {
			case horizontal && plus:
				strip.X, strip.Width = block.X+block.Width, k
				grown.Interior.Width += k
			case horizontal:
				strip.X, strip.Width = block.X-k, k
				grown.Interior.X, grown.Interior.Width = room.Interior.X-k, room.Interior.Width+k
			case plus:
				strip.Z, strip.Height = block.Z+block.Height, k
				grown.Interior.Height += k
			default:
				strip.Z, strip.Height = block.Z-k, k
				grown.Interior.Z, grown.Interior.Height = room.Interior.Z-k, room.Interior.Height+k
			}
			if u.free(strip, false) && u.inset(strip) {
				return grown, true
			}
		}
	}
	return PlannedRoom{}, false
}
