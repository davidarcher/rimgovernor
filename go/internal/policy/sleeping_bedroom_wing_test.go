package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// wingFixture is bedroomFixture with its two bedrooms replaced by a wing of n
// rooms (#2133); both colonists sleep in the barracks, so both are unhoused.
func wingFixture(n int, purpose WingPurpose) (LayoutPlan, RoomObservation, SleepingObservation) {
	plan, rooms, sleeping := bedroomFixture()
	plan.Rooms = plan.Rooms[:1]
	wing := Wing{Purpose: purpose}
	for i := int32(0); i < int32(n); i++ {
		x := 10 + i*6
		wing.Rooms = append(wing.Rooms, PlannedRoom{Role: PlannedBedroom, Interior: Rectangle{X: x, Z: 0, Width: 5, Height: 5}, Door: domain.Cell{X: x + 2, Z: 5}, DoorRot: domain.North})
	}
	plan.Wings = []Wing{wing}
	return plan, rooms, sleeping
}

func wingInteriors(rooms []PlannedRoom) []int32 {
	var xs []int32
	for _, r := range rooms {
		xs = append(xs, r.Interior.X)
	}
	return xs
}

// A wing of unbuilt bedrooms is one step carrying every room in plan order,
// ahead of need: unhoused colonists owe the whole wing (#2133).
func TestBedroomStepCarriesTheWholeWing(t *testing.T) {
	plan, rooms, sleeping := wingFixture(4, WingBedrooms)
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Unhoused != 2 || got.Room.Interior.X != 10 {
		t.Fatalf("step = %+v, want the wing's first room reconciled", got)
	}
	if xs := wingInteriors(got.Rooms); len(xs) != 4 || xs[0] != 10 || xs[1] != 16 || xs[2] != 22 || xs[3] != 28 {
		t.Fatalf("rooms = %v, want all four in plan order", xs)
	}
	if !got.Rooms[0].Same(got.Room) {
		t.Fatal("Room must be Rooms[0]")
	}
}

// A built room with a bed is not reconciled again; a standing empty room is,
// and leads the step as before.
func TestBedroomStepWingBatchSkipsBuiltRooms(t *testing.T) {
	plan, rooms, sleeping := wingFixture(4, WingBedrooms)
	rooms.Rooms = append(rooms.Rooms,
		Room{ID: "built", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"x"}, Cells: roomCells(10, 0, 5, 5)},
		Room{ID: "empty", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: roomCells(22, 0, 5, 5)})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "x", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []PawnID{"z"}, AccessibleTo: []PawnID{"z"}})
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Interior.X != 22 {
		t.Fatalf("step = %+v, want the standing empty room first", got)
	}
	if xs := wingInteriors(got.Rooms); len(xs) != 3 || xs[0] != 16 || xs[1] != 22 || xs[2] != 28 {
		t.Fatalf("rooms = %v, want the wing's unbuilt and empty rooms in plan order", xs)
	}
}

// With nobody unhoused nothing is owed, however empty the wing; a Retiring
// wing is never built out (#1219).
func TestBedroomStepWingOwedOnlyByNeedAndNeverRetiring(t *testing.T) {
	plan, rooms, sleeping := wingFixture(4, WingBedrooms)
	// Both colonists sleep in a bedroom elsewhere: nobody is unhoused.
	rooms.Rooms[0].Role, rooms.Rooms[0].Cells = domain.Known(RoomRoleBedroom), []domain.Cell{{X: 50, Z: 50}}
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{}); got.Kind != BedroomNone {
		t.Fatalf("housed colony = %+v, want no step", got)
	}
	plan, rooms, sleeping = wingFixture(4, WingBedroomsRetiring)
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{}); got.Kind != BedroomNone {
		t.Fatalf("retiring wing = %+v, want none", got)
	}
}

// A bedroom outside any wing stands alone in its step.
func TestBedroomStepSpineRoomStandsAlone(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	sleeping.People[0].OwnedBed = domain.Known("")
	sleeping.Beds[0].Owners = nil
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || len(got.Rooms) != 1 || got.Rooms[0].Interior.X != 10 {
		t.Fatalf("step = %+v, want the one spine room", got)
	}
}

// The batched wing is the first in plan order that owes a bedroom: a standing
// empty room in a later wing does not jump ahead of an unbuilt earlier wing
// (#2139).
func TestBedroomStepChoosesWingInPlanOrder(t *testing.T) {
	plan, rooms, sleeping := wingFixture(3, WingBedrooms)
	second := Wing{Purpose: WingBedrooms, Rooms: []PlannedRoom{{Role: PlannedBedroom, Interior: Rectangle{X: 50, Z: 0, Width: 5, Height: 5}, Door: domain.Cell{X: 52, Z: 5}, DoorRot: domain.North}}}
	plan.Wings = append(plan.Wings, second)
	rooms.Rooms = append(rooms.Rooms, Room{ID: "empty2", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: roomCells(50, 0, 5, 5)})
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Interior.X != 10 {
		t.Fatalf("step = %+v, want wing 1 first", got)
	}
	if xs := wingInteriors(got.Rooms); len(xs) != 3 || xs[0] != 10 || xs[2] != 22 {
		t.Fatalf("rooms = %v, want wing 1's three rooms", xs)
	}
	// A Retiring first wing yields none of its rooms: the next wing leads.
	plan.Wings[0].Purpose = WingBedroomsRetiring
	got = NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Interior.X != 50 || len(got.Rooms) != 1 {
		t.Fatalf("step = %+v, want wing 2 once wing 1 retires", got)
	}
}
