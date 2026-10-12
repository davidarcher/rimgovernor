package policy

import "slices"

// Successor rooms (#2863). A built room the embedder moves is not resized: the
// tiled plan holds a room that Replaces it (EmbeddedBlock.Replaces), the old
// room stays pinned and in use until the successor stands, and then the old room
// is a room the new embedding has no node for (BuiltRegions leaves out a room
// whose successor stands), so the replan retires it and clearance takes its
// ground. A bedroom the successor replaces stays while a pawn owns a bed in it
// (SleptBedrooms, TilePinned), and its sleepers move one pawn a step, each
// straight to a vacant bed (NextMigrateStep).

// superseded are the ids of the plan's rooms that another room of the plan
// replaces.
func (p LayoutPlan) superseded() map[int32]bool {
	out := map[int32]bool{}
	for _, r := range p.Rooms {
		if r.Replaces != 0 {
			out[r.Replaces] = true
		}
	}
	return out
}

// RoomChains is how many rooms of role the plan holds, a room and the
// successors that replace it counting as one.
func (p LayoutPlan) RoomChains(role PlannedRole) int {
	gone := p.superseded()
	n := 0
	for _, r := range p.roomsOf(role) {
		if !gone[r.ID] {
			n++
		}
	}
	return n
}

// SuccessorPending reports a successor whose predecessor is still in the plan:
// a rebuild in flight.
func SuccessorPending(plan LayoutPlan) bool {
	gone := plan.superseded()
	return slices.ContainsFunc(plan.Rooms, func(r PlannedRoom) bool { return gone[r.ID] })
}

// successorPinned are the ids of a room in a rebuild in flight and of its
// successor: neither moves until the predecessor leaves the plan.
func successorPinned(plan LayoutPlan) map[int32]bool {
	have := map[int32]bool{}
	for _, r := range plan.Rooms {
		have[r.ID] = true
	}
	out := map[int32]bool{}
	for _, r := range plan.Rooms {
		if r.Replaces != 0 && have[r.Replaces] {
			out[r.ID], out[r.Replaces] = true, true
		}
	}
	return out
}
