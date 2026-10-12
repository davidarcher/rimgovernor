package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The training range and the sparring ring as station-less room nodes of the
// station graph (#2887). Both are already planned rooms sited by
// the tiler; the nodes are the station planner's input for the same
// rooms. Both are unfenced open-air
// rooms with a fixed floor that rangeBlock/ringBlock keep unchanged, so the
// node asks the exact floor (stands by RangeDistance+1 cells; RingSide
// squared) and linkRooms adds no door threshold to it (exactFloor). No distance band sits
// on them (#2987): the old rangeGap (2 clear cells, "a firing line to keep clear
// of walls") is a judgment value no game rule or model derives, and the reach
// (outskirtsReach) was the old siting window; neither is kept. One constraint
// sits on the nodes:
//
//   - Beside. The ring stands beside the range, a required link as the
//     incinerator stands beside the waste yard.
//
// The tiler writes the range's facing (dummies away from the base, as the
// siting does; nothing behind the dummies is kept free, since a practice shot
// never hits a pawn) and the ring's marker count onto the tiled room.
//
// Both grow by widening in place (widenRange, widenRing): a range gains stands
// along its rows and a ring gains markers in fixed slots, so a standing piece
// never moves.

// ReasonTrainingRoom is why the graph holds a training room: the colony's
// capable adults need a place to train.
const ReasonTrainingRoom = "training_room_demanded"

// TrainingRoomNodes are the range and ring nodes for stands range stands and
// markers ring markers; a count of zero asks for no such room. A first range is
// asked for at least RangeMinStands and a ring for at least RingMinMarkers, as
// the siting does.
func TrainingRoomNodes(stands, markers int) []RoomNode {
	var nodes []RoomNode
	if stands > 0 {
		n := int32(min(max(stands, RangeMinStands), RangeMaxStands))
		nodes = append(nodes, RoomNode{ID: string(PlannedTrainingRange), Role: PlannedTrainingRange, Area: int(n * (RangeDistance + 1)), Width: RangeDistance + 1, Reason: ReasonTrainingRoom})
	}
	if markers > 0 {
		m := min(max(markers, RingMinMarkers), RingMaxMarkers)
		ring := RoomNode{ID: string(PlannedSparringRing), Role: PlannedSparringRing, Area: int(RingSide * RingSide), Width: RingSide, Markers: m, Reason: ReasonTrainingRoom}
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

// apartFromBase are the roles of the rooms the graph sites by their own terms, not
// as part of the base: the outskirts rooms, the training rooms and the shelter.
var apartFromBase = map[PlannedRole]bool{
	PlannedTomb: true, PlannedMorgue: true, PlannedGraveyard: true, PlannedWasteYard: true, PlannedIncinerator: true,
	PlannedTrainingRange: true, PlannedSparringRing: true, PlannedShelter: true,
}

// awayReference is the bounding box of the placed blocks of the groups outside
// the rooms that stand apart (apartFromBase): the base a facing group points away from. False while none is
// placed.
func awayReference(groups []RoomGroup, blocks []Rectangle) (Rectangle, bool) {
	var ref Rectangle
	found := false
	for i, b := range blocks {
		if b.Width == 0 || apartFromBase[groups[i].Planned] {
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
