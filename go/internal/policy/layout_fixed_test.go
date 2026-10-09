package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A planned room with a standing shell or anything of ours on its
// ring or floor is fixed; an untouched one is not.
func TestFixedRoomsAreTheTouchedOnes(t *testing.T) {
	room := func(x int32) PlannedRoom {
		return PlannedRoom{Role: PlannedBedroom, Interior: Rectangle{X: x, Z: 10, Width: 5, Height: 5}}
	}
	standing, onRing, onFloor, untouched := room(10), room(20), room(30), room(40)
	plan := LayoutPlan{Rooms: []PlannedRoom{standing, onRing, untouched}, Wings: []Wing{{Rooms: []PlannedRoom{onFloor}}}}
	occupied := map[domain.Cell]bool{
		{X: 19, Z: 9}:   true, // the ring corner of onRing
		{X: 32, Z: 12}:  true, // inside onFloor
		{X: 100, Z: 12}: true, // nowhere near a room
	}
	fixed := FixedRooms(plan, ringWalls(plan, standing), occupied)
	for _, r := range []PlannedRoom{standing, onRing, onFloor} {
		if !fixed[r.Interior] {
			t.Fatalf("room at x=%d is not fixed", r.Interior.X)
		}
	}
	if fixed[untouched.Interior] || len(fixed) != 3 {
		t.Fatalf("fixed = %v", fixed)
	}
}
