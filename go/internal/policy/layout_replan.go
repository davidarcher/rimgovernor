package policy

// wingPinned reports whether a room of w is pinned.
func wingPinned(w Wing, pins map[int32]bool) bool {
	for _, r := range w.Rooms {
		if pins[r.ID] {
			return true
		}
	}
	return false
}

// allPins is every room of plan, wings included.
func allPins(plan LayoutPlan) map[int32]bool {
	pins := map[int32]bool{}
	for _, r := range plan.AllRooms() {
		pins[r.ID] = true
	}
	return pins
}

func bedroomCount(p LayoutPlan) int {
	n := 0
	for _, r := range p.AllRooms() {
		if r.Role == PlannedBedroom {
			n++
		}
	}
	return n
}
