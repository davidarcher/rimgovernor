package policy

// Duplicate add-on rooms (#1823, epic #1819): the planner only ever added
// rooms, so a room it grew twice (nine worship rooms after a transposed
// frame) stayed in the plan. A role's rooms reduce to one; the rest leave
// the plan, and a built one is no longer planned ground, so clearance
// demolishes it as it does a dropped wing's. Rooms are never shrunk.

// addOnRoomRoles are the roles holding at most one planned room.
var addOnRoomRoles = []ModuleRole{ModuleWorship, ModuleNursery, ModulePlayroom, ModuleClassroom, ModuleDeathrestChamber, ModuleContainmentCell}

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

// BuiltRooms are the interiors of the plan's rooms with a census room
// standing in them.
func BuiltRooms(plan LayoutPlan, rooms RoomObservation) map[Rectangle]bool {
	built := map[Rectangle]bool{}
	for _, r := range plan.AllRooms() {
		if _, ok := PlannedRoomStanding(r, rooms); ok {
			built[r.Interior] = true
		}
	}
	return built
}

// DuplicateRooms is how many add-on rooms the plan holds beyond one per role.
func DuplicateRooms(plan LayoutPlan) int {
	n := 0
	for _, role := range addOnRoomRoles {
		n += max(len(plan.roomsOf(role))-1, 0)
	}
	return n
}

func (p LayoutPlan) roomsOf(role ModuleRole) []LayoutRoom {
	var out []LayoutRoom
	for _, r := range p.AllRooms() {
		if r.Role == role {
			out = append(out, r)
		}
	}
	return out
}

// retireDuplicateRooms keeps one room of each add-on role: a built room
// first, else the smallest that holds the role's shape (shapes), else the
// smallest. It reports whether it dropped any.
func retireDuplicateRooms(plan LayoutPlan, shapes []ChildRoomShape, built map[Rectangle]bool) (LayoutPlan, bool) {
	drop := map[Rectangle]bool{}
	for _, role := range addOnRoomRoles {
		rooms := plan.roomsOf(role)
		if len(rooms) < 2 {
			continue
		}
		keep := -1
		for i, r := range rooms {
			if built[r.Interior] {
				keep = i
				break
			}
		}
		if keep < 0 {
			keep = smallestRoom(rooms, roleShape(shapes, role))
		}
		for i, r := range rooms {
			if i != keep {
				drop[r.Interior] = true
			}
		}
	}
	if len(drop) == 0 {
		return plan, false
	}
	plan.Rooms = withoutRooms(plan.Rooms, drop)
	plan.Wings = append([]Wing(nil), plan.Wings...)
	for i := range plan.Wings {
		plan.Wings[i].Rooms = withoutRooms(plan.Wings[i].Rooms, drop)
	}
	return plan, true
}

func roleShape(shapes []ChildRoomShape, role ModuleRole) *ChildRoomShape {
	for i := range shapes {
		if shapes[i].Module == role {
			return &shapes[i]
		}
	}
	return nil
}

// smallestRoom is the index of the fewest-cell room holding shape, else of
// the fewest-cell room.
func smallestRoom(rooms []LayoutRoom, shape *ChildRoomShape) int {
	best, bestHolds := 0, false
	for i, r := range rooms {
		w, d := frameDims(r)
		holds := shape != nil && shape.holds(w, d)
		if i == 0 {
			best, bestHolds = 0, holds
			continue
		}
		if holds && !bestHolds || holds == bestHolds && r.Interior.Width*r.Interior.Height < rooms[best].Interior.Width*rooms[best].Interior.Height {
			best, bestHolds = i, holds
		}
	}
	return best
}

func withoutRooms(rooms []LayoutRoom, drop map[Rectangle]bool) []LayoutRoom {
	var out []LayoutRoom
	for _, r := range rooms {
		if !drop[r.Interior] {
			out = append(out, r)
		}
	}
	return out
}
