package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The training range and the sparring ring as station-less room nodes of the
// station graph (#2887). Both are already planned rooms sited by
// growRanges/growRings; the nodes are the station planner's input for the same
// rooms, and the old planner stays authoritative. Both are unfenced open-air
// rooms with a fixed floor that rangeBlock/ringBlock keep unchanged, so the
// node asks the exact floor (stands by RangeDistance+1 cells; RingSide
// squared) and linkRooms adds no door threshold to it (exactFloor). Three
// constraints sit on the nodes:
//
//   - Band. Both keep the range's own gap (rangeGap) from the base's other
//     rooms, no more than outskirtsReach beyond them.
//   - Beside. The ring stands beside the range, a required link as the
//     incinerator stands beside the waste yard.
//   - Facing. The range's dummies point away from the base: a hard orientation
//     constraint. The embedding keeps the RangeBackdrop cells behind the
//     dummies free of every other room (backdropClear) and reports a range
//     whose backdrop is taken (ReasonEmbedFacing); the tiler writes the
//     facing (and the ring's marker count) onto the tiled room.
//
// Both grow by widening in place (widenRange, widenRing): a range gains stands
// along its rows and a ring gains markers in fixed slots, so a standing piece
// never moves.

// ReasonTrainingRoom is why the graph holds a training room: the colony's
// capable adults need a place to train.
const ReasonTrainingRoom = "training_room_demanded"

// TrainingBand is the ground a training room keeps from the base's other rooms.
func TrainingBand() DistanceBand { return DistanceBand{Min: rangeGap, Max: outskirtsReach} }

// TrainingRoomNodes are the range and ring nodes for stands range stands and
// markers ring markers; a count of zero asks for no such room. A first range is
// asked for at least RangeMinStands and a ring for at least RingMinMarkers, as
// the siting does.
func TrainingRoomNodes(stands, markers int) []RoomNode {
	var nodes []RoomNode
	if stands > 0 {
		n := int32(min(max(stands, RangeMinStands), RangeMaxStands))
		nodes = append(nodes, RoomNode{ID: string(PlannedTrainingRange), Role: PlannedTrainingRange, Area: int(n * (RangeDistance + 1)), Width: RangeDistance + 1, Band: TrainingBand(), FacesAway: true, Reason: ReasonTrainingRoom})
	}
	if markers > 0 {
		m := min(max(markers, RingMinMarkers), RingMaxMarkers)
		ring := RoomNode{ID: string(PlannedSparringRing), Role: PlannedSparringRing, Area: int(RingSide * RingSide), Width: RingSide, Band: TrainingBand(), Markers: m, Reason: ReasonTrainingRoom}
		if stands > 0 {
			ring.Beside = string(PlannedTrainingRange)
		}
		nodes = append(nodes, ring)
	}
	return nodes
}

// exactFloor reports a role whose floor is its exact shape (a range's rows, the
// ring's square): a required link adds no door threshold to it, for it has no
// wall to open a door in.
func exactFloor(role PlannedRole) bool {
	return role == PlannedTrainingRange || role == PlannedSparringRing
}

// placedBlocks are the embedding's interiors indexed by group (the zero
// rectangle for a group left out).
func placedBlocks(groups []RoomGroup, blocks []EmbeddedBlock) []Rectangle {
	out := make([]Rectangle, len(groups))
	for _, b := range blocks {
		if b.Group >= 0 && b.Group < len(out) {
			out[b.Group] = b.Interior
		}
	}
	return out
}

// awayReference is the bounding box of the placed blocks of the groups outside
// the band: the base a facing group points away from. False while none is
// placed.
func awayReference(groups []RoomGroup, blocks []Rectangle) (Rectangle, bool) {
	var ref Rectangle
	found := false
	for i, b := range blocks {
		if b.Width == 0 || groups[i].Band.Set() {
			continue
		}
		if !found {
			ref, found = b, true
			continue
		}
		x0, z0 := min(ref.X, b.X), min(ref.Z, b.Z)
		x1, z1 := max(ref.X+ref.Width, b.X+b.Width), max(ref.Z+ref.Height, b.Z+b.Height)
		ref = Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}
	}
	return ref, found
}

// dummyFacing is the direction the dummies of a range at r point: along the
// axis the rows are not on, toward the end farther from ref's centre (a tie
// points to the + end). A range's rows run along X when its interior is taller
// than wide (stands never reach RangeDistance+1).
func dummyFacing(r, ref Rectangle) domain.Rotation {
	if r.Height > r.Width {
		if 2*r.Z+r.Height >= 2*ref.Z+ref.Height {
			return domain.North
		}
		return domain.South
	}
	if 2*r.X+r.Width >= 2*ref.X+ref.Width {
		return domain.East
	}
	return domain.West
}

// backdropStrip is the RangeBackdrop cells behind the dummies of a range at r
// facing facing, past its one-cell margin (the part of rangeBlock beyond the
// padded interior).
func backdropStrip(r Rectangle, facing domain.Rotation) Rectangle {
	p := pad(r, 1)
	switch facing {
	case domain.North:
		return Rectangle{X: p.X, Z: p.Z + p.Height, Width: p.Width, Height: RangeBackdrop}
	case domain.South:
		return Rectangle{X: p.X, Z: p.Z - RangeBackdrop, Width: p.Width, Height: RangeBackdrop}
	case domain.East:
		return Rectangle{X: p.X + p.Width, Z: p.Z, Width: RangeBackdrop, Height: p.Height}
	}
	return Rectangle{X: p.X - RangeBackdrop, Z: p.Z, Width: RangeBackdrop, Height: p.Height}
}

// intersects reports rectangles that share a cell.
func intersects(a, b Rectangle) bool {
	return axisGap(a.X, a.Width, b.X, b.Width) < 0 && axisGap(a.Z, a.Height, b.Z, b.Height) < 0
}

// groupFootprint is the cells group g's block at r occupies: its interior with the
// wall ring an enclosed room owes (an unfenced room owes none).
func groupFootprint(g RoomGroup, r Rectangle) Rectangle {
	if (PlannedRoom{Role: g.Planned}).Unfenced() {
		return r
	}
	return pad(r, 1)
}
