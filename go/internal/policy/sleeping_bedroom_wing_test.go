package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// wingFixture is bedroomFixture with its two bedrooms replaced by a wing of n
// rooms; both colonists sleep in the barracks, so both are unhoused.
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

// A wing of unbuilt bedrooms yields one bedroom per step, the first in plan
// order; unhoused colonists owe the wing.
func TestBedroomStepIsOneRoomPerStep(t *testing.T) {
	plan, rooms, sleeping := wingFixture(4, WingBedrooms)
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Unhoused != 2 || got.Room.Interior.X != 10 {
		t.Fatalf("step = %+v, want the wing's first room reconciled", got)
	}
}

// A built room with a bed is not reconciled again; a standing empty room is
// owed like an unbuilt one, in plan order.
func TestBedroomStepSkipsBuiltRooms(t *testing.T) {
	plan, rooms, sleeping := wingFixture(4, WingBedrooms)
	rooms.Rooms = append(rooms.Rooms,
		Room{ID: "built", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"x"}, Cells: roomCells(10, 0, 5, 5)},
		Room{ID: "empty", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: roomCells(22, 0, 5, 5)})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "x", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []PawnID{"z"}, AccessibleTo: []PawnID{"z"}})
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Interior.X != 16 {
		t.Fatalf("step = %+v, want the first unbuilt room", got)
	}
}

// With nobody unhoused nothing is owed, however empty the wing; a Retiring
// wing is never built out.
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
	if got.Kind != BedroomReconcile || got.Room.Interior.X != 10 {
		t.Fatalf("step = %+v, want the one spine room", got)
	}
}

// The step is the first room in plan order that owes a bedroom: a standing
// empty room in a later wing does not jump ahead of an unbuilt earlier one.
func TestBedroomStepChoosesRoomInPlanOrder(t *testing.T) {
	plan, rooms, sleeping := wingFixture(3, WingBedrooms)
	second := Wing{Purpose: WingBedrooms, Rooms: []PlannedRoom{{Role: PlannedBedroom, Interior: Rectangle{X: 50, Z: 0, Width: 5, Height: 5}, Door: domain.Cell{X: 52, Z: 5}, DoorRot: domain.North}}}
	plan.Wings = append(plan.Wings, second)
	rooms.Rooms = append(rooms.Rooms, Room{ID: "empty2", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: roomCells(50, 0, 5, 5)})
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Interior.X != 10 {
		t.Fatalf("step = %+v, want wing 1 first", got)
	}
	// A Retiring first wing yields none of its rooms: the next wing leads.
	plan.Wings[0].Purpose = WingBedroomsRetiring
	got = NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Interior.X != 50 {
		t.Fatalf("step = %+v, want wing 2 once wing 1 retires", got)
	}
}
