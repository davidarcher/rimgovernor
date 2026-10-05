package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A tier bump retires the smaller wing and sites a new wing at the new
// size, keeping every old room in place (#1219).
func TestTierBumpRetiresWingAndSitesNewOne(t *testing.T) {
	p := corePlan(coreTestZones(), 4, BuildTierCamp)
	old := testBedroomWing(t, p)
	g := growPlan(p, 4, 1, BuildTierPowered)
	var retiring, active []Wing
	for _, w := range g.Wings {
		switch w.Purpose {
		case WingBedroomsRetiring:
			retiring = append(retiring, w)
		case WingBedrooms:
			active = append(active, w)
		}
	}
	if len(retiring) != 1 || len(retiring[0].Rooms) != len(old.Rooms) {
		t.Fatalf("retiring %+v, want the old wing", retiring)
	}
	for i, r := range old.Rooms {
		if !retiring[0].Rooms[i].Same(r) {
			t.Fatal("retired room moved", r)
		}
	}
	if len(active) != 1 || len(active[0].Rooms) != wingMaxRooms {
		t.Fatalf("active wings %+v, want one full wing", active)
	}
	for _, r := range active[0].Rooms {
		if r.Interior.Width*r.Interior.Height != 16 {
			t.Fatal("new wing room not 4x4", r.Interior)
		}
		for _, o := range old.Rooms {
			if rectsOverlap(r.Interior, roomWalls(o)) {
				t.Fatal("new room on retiring room", r.Interior, o.Interior)
			}
		}
	}
	if _, err := CheckRoutes(g); err != nil {
		t.Fatal("routes:", err)
	}
	// The retiring wing stays as it is.
	for _, w := range growPlan(g, 6, 1, BuildTierPowered).Wings {
		if w.Purpose == WingBedroomsRetiring && len(w.Rooms) != len(old.Rooms) {
			t.Fatal("retiring wing grew", len(w.Rooms))
		}
	}
}

func migrateFixture() (LayoutPlan, RoomObservation, SleepingObservation) {
	room := func(x int32) PlannedRoom {
		return PlannedRoom{Role: PlannedBedroom, Interior: Rectangle{X: x, Z: 0, Width: 4, Height: 4}}
	}
	plan := LayoutPlan{Wings: []Wing{
		{Purpose: WingBedroomsRetiring, Rooms: []PlannedRoom{room(0), room(10)}},
		{Purpose: WingBedrooms, Rooms: []PlannedRoom{room(20), room(30)}},
	}}
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

// Pawns leave a Retiring wing one at a time; the move is no bedroom
// deficit (#1219).
func TestMigrateStepMovesOnePawnAtATime(t *testing.T) {
	plan, rooms, sleeping := migrateFixture()
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{}); got.Kind != BedroomNone || got.Unhoused != 0 {
		t.Fatalf("bedroom step %+v, want none: retiring rooms house their pawns", got)
	}
	if got := NextMigrateStep(plan, rooms, sleeping); got.Kind != BedroomReconcile || got.Room.Interior.X != 20 {
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
	if got := NextMigrateStep(plan, rooms, sleeping); got.Kind != BedroomReconcile || got.Room.Interior.X != 30 {
		t.Fatalf("next mover = %+v, want the next active room's shell", got)
	}
	// A vacated retiring bed takes no one new.
	sleeping.People[1].OwnedBed = domain.Known("barracks")
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil, RoomGate{}); got.Kind == BedroomMove && got.Bed == "ob1" {
		t.Fatal("unhoused pawn moved into a retiring room")
	}
	sleeping.People[1].OwnedBed = domain.Known("nb2")
	if got := NextMigrateStep(plan, rooms, sleeping); got.Kind != BedroomNone {
		t.Fatalf("empty retiring wing = %+v, want none", got)
	}
}
