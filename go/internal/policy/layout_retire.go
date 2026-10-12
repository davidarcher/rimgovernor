package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Duplicate add-on rooms: the planner only ever added
// rooms, so a room it grew twice (nine worship rooms after a transposed
// frame) stayed in the plan. A role's rooms reduce to one; the rest leave
// the plan, and a built one is no longer planned ground, so clearance
// demolishes it as it does a dropped wing's. Rooms are never shrunk.

// addOnRoomRoles are the roles holding at most one planned room.
var addOnRoomRoles = []PlannedRole{PlannedWorship, PlannedNursery, PlannedPlayroom, PlannedClassroom, PlannedDeathrestChamber, PlannedContainmentCell, PlannedIsolationRoom}

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

// DuplicateRooms is how many add-on rooms the plan holds beyond one per role.
func DuplicateRooms(plan LayoutPlan) int {
	n := 0
	for _, role := range addOnRoomRoles {
		n += max(len(plan.roomsOf(role))-1, 0)
	}
	return n
}

// retireAddOnRooms drops the add-on rooms the plan no longer needs: the
// duplicates beyond one per role (when built is known) and the unbuilt rooms
// of an ended role (when inUse is known). It reports whether it dropped any.
func retireAddOnRooms(plan LayoutPlan, growth RoomGrowth) (LayoutPlan, bool) {
	drop := map[int32]bool{}
	if growth.Built != nil {
		duplicateRooms(plan, growth.Shapes, growth.Built, drop)
	}
	if growth.InUse != nil {
		endedRooms(plan, growth.Ended, growth.InUse, drop)
	}
	return dropRooms(plan, drop)
}

// duplicateRooms adds to drop all but one room of each add-on role: a built
// room first, else the smallest that holds the role.s shape (shapes), else
// the smallest.
func duplicateRooms(plan LayoutPlan, shapes []ChildRoomShape, built map[int32]bool, drop map[int32]bool) {
	for _, role := range addOnRoomRoles {
		rooms := plan.roomsOf(role)
		if len(rooms) < 2 {
			continue
		}
		keep := -1
		for i, r := range rooms {
			if built[r.ID] {
				keep = i
				break
			}
		}
		if keep < 0 {
			keep = smallestRoom(rooms, roleShape(shapes, role))
		}
		for i, r := range rooms {
			if i != keep {
				drop[r.ID] = true
			}
		}
	}
}

func roleShape(shapes []ChildRoomShape, role PlannedRole) *ChildRoomShape {
	for i := range shapes {
		if shapes[i].Module == role {
			return &shapes[i]
		}
	}
	return nil
}

// smallestRoom is the index of the fewest-cell room holding shape, else of
// the fewest-cell room.
func smallestRoom(rooms []PlannedRoom, shape *ChildRoomShape) int {
	best, bestHolds := 0, false
	for i, r := range rooms {
		w, d := frameDims(r)
		holds := shape != nil && shape.holds(w, d)
		if i == 0 {
			best, bestHolds = 0, holds
			continue
		}
		if holds && !bestHolds || holds == bestHolds && r.Region.Bounds().Width*r.Region.Bounds().Height < rooms[best].Region.Bounds().Width*rooms[best].Region.Bounds().Height {
			best, bestHolds = i, holds
		}
	}
	return best
}

// endedRooms adds to drop each planned room of an ended role that is not in
// use.
func endedRooms(plan LayoutPlan, ended []PlannedRole, inUse map[int32]bool, drop map[int32]bool) {
	for _, role := range ended {
		for _, r := range plan.roomsOf(role) {
			if !inUse[r.ID] {
				drop[r.ID] = true
			}
		}
	}
}

// Surplus rooms: a throne room the title outgrew, a gear
// room whose demand is gone and an extra storage room with no demand leave
// the plan while unbuilt. Rooms are never shrunk; a smaller built throne room
// stays until a room holding the title's area is itself built, then goes to
// clearance. The core storage room and built gear and storage rooms stay.

// SurplusRoomsPossible reports whether retireSurplusRooms could drop a room
// of plan: several throne rooms under a title that asks for one, a gear room
// whose demand reads false, or an extra storage room under an idle reading.
func SurplusRoomsPossible(plan LayoutPlan, throneMin int, demand RoomDemand) bool {
	if throneMin > 0 && len(plan.roomsOf(PlannedThrone)) > 1 {
		return true
	}
	if demand.Known && (!demand.Armory && len(plan.roomsOf(PlannedArmory)) > 0 || !demand.Wardrobe && len(plan.roomsOf(PlannedWardrobe)) > 0) {
		return true
	}
	return demand.StorageIdle && plan.RoomChains(PlannedStorage) > 1
}

// retireSurplusRooms drops the surplus rooms of growth that are unbuilt, plus
// the built throne rooms a built larger one replaces and the built room whose
// built successor stands (layout_succession.go). It reports whether it
// dropped any.
func retireSurplusRooms(plan LayoutPlan, growth RoomGrowth, built map[int32]bool) (LayoutPlan, bool) {
	drop := map[int32]bool{}
	area := func(r PlannedRoom) int { return int(r.Region.Bounds().Width) * int(r.Region.Bounds().Height) }
	if thrones := plan.roomsOf(PlannedThrone); growth.ThroneMin > 0 && len(thrones) > 1 {
		// Keep a built room that holds the title's area, else the smallest.
		keep := -1
		for i, r := range thrones {
			switch {
			case area(r) < growth.ThroneMin:
			case built[r.ID]:
				if keep < 0 || !built[thrones[keep].ID] {
					keep = i
				}
			case keep < 0 || !built[thrones[keep].ID] && area(r) < area(thrones[keep]):
				keep = i
			}
		}
		if keep >= 0 {
			for i, r := range thrones {
				if i != keep && (built[thrones[keep].ID] || !built[r.ID]) {
					drop[r.ID] = true
				}
			}
		}
	}
	if growth.Demand.Known {
		for _, role := range gearRooms {
			if role == PlannedArmory && growth.Demand.Armory || role == PlannedWardrobe && growth.Demand.Wardrobe {
				continue
			}
			for _, r := range plan.roomsOf(role) {
				if !built[r.ID] {
					drop[r.ID] = true
				}
			}
		}
	}
	if growth.Demand.StorageIdle {
		// A rebuild in flight is not an extra room: its pair stays.
		pinned := successorPinned(plan)
		for i, r := range plan.roomsOf(PlannedStorage) {
			if i > 0 && !built[r.ID] && !pinned[r.ID] {
				drop[r.ID] = true
			}
		}
	}
	retireSuperseded(plan, built, drop)
	return dropRooms(plan, drop)
}

// RoomsOfRoles is how many planned rooms hold one of roles.
func RoomsOfRoles(plan LayoutPlan, roles []PlannedRole) int {
	n := 0
	for _, role := range roles {
		n += len(plan.roomsOf(role))
	}
	return n
}
