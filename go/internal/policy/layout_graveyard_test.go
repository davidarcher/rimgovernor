package policy

import "testing"

// graveyardPlan is a plan holding the outskirts cluster's rooms (tomb, morgue,
// graveyard, waste yard) in the slots of an outline.
func graveyardPlan(t *testing.T) (LayoutPlan, PlannedRoom) {
	t.Helper()
	size := OutskirtsSize()
	layout, ok := OutskirtsSlots(Rectangle{X: 10, Z: 10, Width: size[0], Height: size[1]})
	if !ok {
		t.Fatal("no outskirts layout")
	}
	plan := LayoutPlan{Rooms: outskirtsRooms(layout)}.WithRoomIDs()
	rooms := plan.roomsOf(PlannedGraveyard)
	if len(rooms) != 1 {
		t.Fatalf("graveyards %d", len(rooms))
	}
	return plan, rooms[0]
}
