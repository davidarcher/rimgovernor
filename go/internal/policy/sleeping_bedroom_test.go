package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func bedroomFixture() (LayoutPlan, RoomObservation, SleepingObservation) {
	plan := LayoutPlan{Rooms: []LayoutRoom{
		{Role: ModuleBarracks, Interior: Rectangle{X: 0, Z: 0, Width: 7, Height: 7}, DoorRot: domain.North},
		{Role: ModuleBedroom, Interior: Rectangle{X: 10, Z: 0, Width: 5, Height: 5}, Door: domain.Cell{X: 12, Z: 5}, DoorRot: domain.North},
		{Role: ModuleBedroom, Interior: Rectangle{X: 16, Z: 0, Width: 5, Height: 5}, Door: domain.Cell{X: 18, Z: 5}, DoorRot: domain.North},
	}}
	bed := func(id string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Roofed: domain.Known(true), Owners: owners, AccessibleTo: []PawnID{"a", "b"}}
	}
	rooms := RoomObservation{Rooms: []Room{{ID: "barracks", Role: domain.Known(RoomRoleBarracks), Enclosed: domain.Known(true), Beds: []string{"b1", "b2", "b3"}, Cells: []domain.Cell{{X: 3, Z: 3}}}}}
	sleeping := SleepingObservation{Colonists: 2,
		People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("b1")}, {ID: "b", OwnedBed: domain.Known("b2")}},
		Beds:   []SleepingBed{bed("b1", "a"), bed("b2", "b"), bed("b3")}}
	return plan, rooms, sleeping
}

func TestBedroomStepShellsFurnishesThenMoves(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	if got := NextBedroomStep(plan, rooms, sleeping, nil); got.Kind != BedroomShell || got.Room.Interior.X != 10 {
		t.Fatalf("first step = %+v, want the first slot's shell", got)
	}
	var cells []domain.Cell
	for x := int32(10); x < 15; x++ {
		for z := int32(0); z < 5; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	rooms.Rooms = append(rooms.Rooms, Room{ID: "r1", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: cells})
	if got := NextBedroomStep(plan, rooms, sleeping, nil); got.Kind != BedroomFurnish || len(got.Cells) != 25 {
		t.Fatalf("standing empty bedroom = %+v, want furnish", got)
	}
	rooms.Rooms[1].Role, rooms.Rooms[1].Beds = domain.Known(RoomRoleBedroom), []string{"r1bed"}
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "r1bed", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), AccessibleTo: []PawnID{"a", "b"}})
	got := NextBedroomStep(plan, rooms, sleeping, nil)
	if got.Kind != BedroomMove || got.Pawn != "a" || got.Bed != "r1bed" || got.PreviousBed != "b1" {
		t.Fatalf("vacant bedroom bed = %+v, want a moved from b1", got)
	}
	sleeping.People[0].OwnedBed = domain.Known("r1bed")
	sleeping.Beds[3].Owners = []PawnID{"a"}
	if got := NextBedroomStep(plan, rooms, sleeping, nil); got.Kind != BedroomShell || got.Room.Interior.X != 16 {
		t.Fatalf("second colonist = %+v, want the second slot's shell", got)
	}
}

func TestBedroomStepWaitsForEveryoneToOwnABed(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	sleeping.People[1].OwnedBed = domain.Known("")
	if got := NextBedroomStep(plan, rooms, sleeping, nil); got.Kind != BedroomNone {
		t.Fatalf("unbedded colonist = %+v, want barracks first", got)
	}
	if owed, known := BedroomsOwed(domain.Known(plan), domain.Unknown[RoomObservation](), domain.Known(sleeping), nil).Value(); known || owed {
		t.Fatal("unknown rooms must leave the deficit unknown")
	}
}

func TestBedroomStepKeepsNeverUpgradeOwner(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	rooms.Rooms = append(rooms.Rooms, Room{ID: "r1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"r1bed"}})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "r1bed", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), AccessibleTo: []PawnID{"a", "b"}})
	sleeping.Beds[0].Room = domain.Known("plain")
	sleeping.Beds[1].Room = domain.Known("barracks")
	got := NextBedroomStep(plan, rooms, sleeping, map[string]RoomTarget{"plain": {NeverUpgrade: true}})
	if got.Kind != BedroomMove || got.Pawn != "b" || got.Bed != "r1bed" {
		t.Fatalf("ascetic in a never-upgrade room = %+v, want b moved instead (#826)", got)
	}
}

func TestBedroomStepNeverSplitsACouple(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	rooms.Rooms = append(rooms.Rooms, Room{ID: "r1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"r1bed"}})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "r1bed", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), AccessibleTo: []PawnID{"a", "b"}})
	sleeping.People[0].Partners, sleeping.People[0].BedSharingAllowed = []PawnID{"b"}, domain.Known(true)
	sleeping.People[1].Partners, sleeping.People[1].BedSharingAllowed = []PawnID{"a"}, domain.Known(true)
	if got := NextBedroomStep(plan, rooms, sleeping, nil); got.Kind == BedroomMove {
		t.Fatalf("couple = %+v, want neither moved into a single bedroom (#838)", got)
	}
}
