package policy

import (
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Room ids are unique, handed out in plan order from the latched counter,
// never reused after a room leaves, and kept by a JSON round trip.
func TestRoomIDsAreUniqueStableAndNeverReused(t *testing.T) {
	room := func(x int32) PlannedRoom {
		return PlannedRoom{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: x, Z: 0, Width: 3, Height: 3}), DoorRot: domain.South}
	}
	plan := LayoutPlan{Spine: []SpineSegment{{From: domain.Cell{X: 0, Z: 5}, To: domain.Cell{X: 40, Z: 5}}}, Rooms: []PlannedRoom{room(0), room(10)}, Wings: []Wing{{Purpose: WingSuites, Rooms: []PlannedRoom{room(20)}}}}
	plan = plan.WithRoomIDs()
	for i, r := range plan.AllRooms() {
		if r.ID != int32(i+1) {
			t.Fatalf("room %d has id %d", i, r.ID)
		}
	}
	if !plan.Valid() || plan.NextRoomID != 4 {
		t.Fatalf("valid=%v next=%d", plan.Valid(), plan.NextRoomID)
	}
	if again := plan.WithRoomIDs(); !reflect.DeepEqual(again, plan) {
		t.Fatal("assigning ids again changed the plan")
	}
	// A room leaves, another arrives: the freed id is not handed out again.
	plan.Rooms = append(plan.Rooms[1:], room(30))
	plan = plan.WithRoomIDs()
	if got := plan.Rooms[len(plan.Rooms)-1].ID; got != 4 || plan.NextRoomID != 5 {
		t.Fatalf("new room id %d next %d, want 4 and 5", got, plan.NextRoomID)
	}
	// A successor links to the room it replaces.
	plan.Rooms[len(plan.Rooms)-1].Replaces = 1
	if !plan.Valid() {
		t.Fatal("a successor linked through Replaces is invalid")
	}
	out, err := json.Marshal(plan)
	if err != nil {
		t.Fatal(err)
	}
	var back LayoutPlan
	if err := json.Unmarshal(out, &back); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(back, plan) {
		t.Fatalf("round trip changed the plan: %s", out)
	}
	dup := plan
	dup.Rooms = append([]PlannedRoom(nil), plan.Rooms...)
	dup.Rooms[0].ID = dup.Rooms[1].ID
	if dup.Valid() {
		t.Fatal("duplicate ids are valid")
	}
}

// A derive gives unique ids, and the same
// input gives the same ids.
func TestDeriveGivesStableRoomIDs(t *testing.T) {
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := derivePlan(s, 3, nil)
	if !ok || !plan.Valid() {
		t.Fatal("no valid plan")
	}
	again, _ := derivePlan(s, 3, nil)
	if !reflect.DeepEqual(plan, again) {
		t.Fatal("same input, different ids")
	}
}
