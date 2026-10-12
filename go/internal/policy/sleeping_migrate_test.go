package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func migrateFixture() (LayoutPlan, RoomObservation, SleepingObservation) {
	room := func(id, x, replaces int32) PlannedRoom {
		return PlannedRoom{ID: id, Replaces: replaces, Role: PlannedBedroom, Region: RectRegion(Rectangle{X: x, Z: 0, Width: 4, Height: 4})}
	}
	// Two bedrooms and the two successors that replace them.
	plan := LayoutPlan{Rooms: []PlannedRoom{room(1, 0, 0), room(2, 10, 0), room(3, 20, 1), room(4, 30, 2)}}
	standing := func(id string, x int32, beds ...string) Room {
		return Room{ID: id, Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: beds, Cells: []domain.Cell{{X: x + 2, Z: 2}}}
	}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{standing("o1", 0, "ob1"), standing("o2", 10, "ob2"), standing("n1", 20)}}
	bed := func(id string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: owners, AccessibleTo: []PawnID{"a", "b"}}
	}
	sleeping := SleepingObservation{Colonists: 2,
		People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("ob1")}, {ID: "b", OwnedBed: domain.Known("ob2")}},
		Beds:   []SleepingBed{bed("ob1", "a"), bed("ob2", "b")}}
	return plan, rooms, sleeping
}

// Pawns leave a replaced bedroom one at a time; the move is no bedroom
// deficit.
func TestMigrateStepMovesOnePawnAtATime(t *testing.T) {
	plan, rooms, sleeping := migrateFixture()
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{}); got.Kind != BedroomNone || got.Unhoused != 0 {
		t.Fatalf("bedroom step %+v, want none: retiring rooms house their pawns", got)
	}
	if got := NextMigrateStep(plan, rooms, sleeping); got.Kind != BedroomReconcile || got.Room.Region.Bounds().X != 20 {
		t.Fatalf("empty active room = %+v, want furnish", got)
	}
	rooms.Rooms[2].Beds = []string{"nb1"}
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "nb1", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), AccessibleTo: []PawnID{"a", "b"}})
	got := NextMigrateStep(plan, rooms, sleeping)
	if got.Kind != BedroomMove || got.Pawn != "a" || got.Bed != "nb1" || got.PreviousBed != "ob1" {
		t.Fatalf("vacant active bed = %+v, want a moved", got)
	}
	sleeping.People[0].OwnedBed = domain.Known("nb1")
	sleeping.Beds[2].Owners, sleeping.Beds[0].Owners = []PawnID{"a"}, nil
	if got := NextMigrateStep(plan, rooms, sleeping); got.Kind != BedroomReconcile || got.Room.Region.Bounds().X != 30 {
		t.Fatalf("next mover = %+v, want the next active room's shell", got)
	}
	// A vacated retiring bed takes no one new.
	sleeping.People[1].OwnedBed = domain.Known("barracks")
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{}); got.Kind == BedroomMove && got.Bed == "ob1" {
		t.Fatal("unhoused pawn moved into a retiring room")
	}
	sleeping.People[1].OwnedBed = domain.Known("nb2")
	if got := NextMigrateStep(plan, rooms, sleeping); got.Kind != BedroomNone {
		t.Fatalf("no retiring pawn left = %+v, want none", got)
	}
}
