package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Rooms leave the plan when the tiled replan has no node for them
// (ReplanLayout): the duplicate add-on rooms, the rooms of an ended role, the
// surplus throne, gear and storage rooms are rooms the graph does not ask for.
// These are the reads the review keeps for it.

// ChildRoomShapes are the shapes of every need whose furniture resolves,
// planned or not.
func ChildRoomShapes(needs []ChildRoomNeed, defs []FurnitureDefinition) []ChildRoomShape {
	var out []ChildRoomShape
	for _, n := range needs {
		if s, ok := n.shape(defs); ok {
			out = append(out, s)
		}
	}
	return out
}

// InFlightRoomCells are the walls of the plan's rooms an open journal plan
// is working on, keyed by the room's interior origin as the plan's method
// ids are: added to the occupied cells, they make such a room fixed
// before any building of it is accepted.
func InFlightRoomCells(plan LayoutPlan, origins map[domain.Cell]bool) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, r := range plan.AllRooms() {
		if origins[domain.Cell{X: r.Region.Bounds().X, Z: r.Region.Bounds().Z}] {
			for _, c := range rectCells(roomWalls(r)) {
				out[c] = true
			}
		}
	}
	return out
}

// RoomsOfRoles is how many planned rooms hold one of roles.
func RoomsOfRoles(plan LayoutPlan, roles []PlannedRole) int {
	n := 0
	for _, role := range roles {
		n += len(plan.roomsOf(role))
	}
	return n
}
