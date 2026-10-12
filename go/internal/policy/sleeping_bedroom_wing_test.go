package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// wingFixture is bedroomFixture with its two bedrooms replaced by a row of n
// rooms; both colonists sleep in the barracks, so both are unhoused.
func wingFixture(n int) (LayoutPlan, RoomObservation, SleepingObservation) {
	plan, rooms, sleeping := bedroomFixture()
	plan.Rooms = plan.Rooms[:1]
	for i := int32(0); i < int32(n); i++ {
		x := 10 + i*6
		plan.Rooms = append(plan.Rooms, PlannedRoom{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: x, Z: 0, Width: 5, Height: 5}), Door: domain.Cell{X: x + 2, Z: 5}, DoorRot: domain.North})
	}
	return plan, rooms, sleeping
}

// A wing of unbuilt bedrooms yields one bedroom per step, the first in plan
// order; unhoused colonists owe the wing.
func TestBedroomStepIsOneRoomPerStep(t *testing.T) {
	plan, rooms, sleeping := wingFixture(4)
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Unhoused != 2 || got.Room.Region.Bounds().X != 10 {
		t.Fatalf("step = %+v, want the wing's first room reconciled", got)
	}
}

// A built room with a bed is not reconciled again; a standing empty room is
// owed like an unbuilt one, in plan order.
func TestBedroomStepSkipsBuiltRooms(t *testing.T) {
	plan, rooms, sleeping := wingFixture(4)
	rooms.Rooms = append(rooms.Rooms,
		Room{ID: "built", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"x"}, Cells: roomCells(10, 0, 5, 5)},
		Room{ID: "empty", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: roomCells(22, 0, 5, 5)})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "x", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []PawnID{"z"}, AccessibleTo: []PawnID{"z"}})
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Region.Bounds().X != 16 {
		t.Fatalf("step = %+v, want the first unbuilt room", got)
	}
}

// With nobody unhoused nothing is owed, however empty the row.
func TestBedroomStepWingOwedOnlyByNeed(t *testing.T) {
	plan, rooms, sleeping := wingFixture(4)
	// Both colonists sleep in a bedroom elsewhere: nobody is unhoused.
	rooms.Rooms[0].Role, rooms.Rooms[0].Cells = domain.Known(RoomRoleBedroom), []domain.Cell{{X: 50, Z: 50}}
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{}); got.Kind != BedroomNone {
		t.Fatalf("housed colony = %+v, want no step", got)
	}
}

// A lone bedroom stands alone in its step.
func TestBedroomStepSpineRoomStandsAlone(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	sleeping.People[0].OwnedBed = domain.Known("")
	sleeping.Beds[0].Owners = nil
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Region.Bounds().X != 10 {
		t.Fatalf("step = %+v, want the one room", got)
	}
}

// The step is the first room in plan order that owes a bedroom: a standing
// empty room in a later room does not jump ahead of an unbuilt earlier one.
func TestBedroomStepChoosesRoomInPlanOrder(t *testing.T) {
	plan, rooms, sleeping := wingFixture(3)
	plan.Rooms = append(plan.Rooms, PlannedRoom{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 50, Z: 0, Width: 5, Height: 5}), Door: domain.Cell{X: 52, Z: 5}, DoorRot: domain.North})
	rooms.Rooms = append(rooms.Rooms, Room{ID: "empty2", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: roomCells(50, 0, 5, 5)})
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{})
	if got.Kind != BedroomReconcile || got.Room.Region.Bounds().X != 10 {
		t.Fatalf("step = %+v, want the first room", got)
	}
}
