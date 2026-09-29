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
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomShell || got.Room.Interior.X != 10 {
		t.Fatalf("first step = %+v, want the first slot's shell", got)
	}
	var cells []domain.Cell
	for x := int32(10); x < 15; x++ {
		for z := int32(0); z < 5; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	rooms.Rooms = append(rooms.Rooms, Room{ID: "r1", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: cells})
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomFurnish || len(got.Cells) != 25 {
		t.Fatalf("standing empty bedroom = %+v, want furnish", got)
	}
	rooms.Rooms[1].Role, rooms.Rooms[1].Beds = domain.Known(RoomRoleBedroom), []string{"r1bed"}
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "r1bed", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), AccessibleTo: []PawnID{"a", "b"}})
	got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil)
	if got.Kind != BedroomMove || got.Pawn != "a" || got.Bed != "r1bed" || got.PreviousBed != "b1" {
		t.Fatalf("vacant bedroom bed = %+v, want a moved from b1", got)
	}
	sleeping.People[0].OwnedBed = domain.Known("r1bed")
	sleeping.Beds[3].Owners = []PawnID{"a"}
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomShell || got.Room.Interior.X != 16 {
		t.Fatalf("second colonist = %+v, want the second slot's shell", got)
	}
}

func TestBedroomStepWaitsForEveryoneToOwnABed(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	sleeping.People[1].OwnedBed = domain.Known("")
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomNone {
		t.Fatalf("unbedded colonist = %+v, want barracks first", got)
	}
	if owed, known := BedroomsOwed(domain.Known(plan), domain.Unknown[RoomObservation](), domain.Known(sleeping), nil, nil, nil).Value(); known || owed {
		t.Fatal("unknown rooms must leave the deficit unknown")
	}
}

func TestBedroomStepKeepsNeverUpgradeOwner(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	rooms.Rooms = append(rooms.Rooms, Room{ID: "r1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"r1bed"}})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "r1bed", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), AccessibleTo: []PawnID{"a", "b"}})
	sleeping.Beds[0].Room = domain.Known("plain")
	sleeping.Beds[1].Room = domain.Known("barracks")
	got := NextBedroomStep(plan, rooms, sleeping, map[string]RoomTarget{"plain": {NeverUpgrade: true}}, nil, nil)
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
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind == BedroomMove {
		t.Fatalf("couple = %+v, want neither moved into a single bedroom (#838)", got)
	}
}

// Spot owners in the starter shell (the planned storage room) move out one
// by one into doored bedrooms; each spot left behind is cleared, and the
// last spot in the shell never reads as a bedroom (#1182).
func TestBedroomStepMovesSpotOwnersOutOfTheShell(t *testing.T) {
	plan, _, sleeping := bedroomFixture()
	plan.Rooms[0] = LayoutRoom{Role: ModuleStorage, Interior: Rectangle{X: 0, Z: 0, Width: 7, Height: 7}, DoorRot: domain.North}
	room := func(id string, x int32, beds ...string) Room {
		var cells []domain.Cell
		for cx := x; cx < x+5; cx++ {
			for z := int32(0); z < 5; z++ {
				cells = append(cells, domain.Cell{X: cx, Z: z})
			}
		}
		role := RoomRole("None")
		if len(beds) > 0 {
			role = RoomRoleBedroom
		}
		return Room{ID: id, Role: domain.Known(role), Enclosed: domain.Known(true), Beds: beds, Cells: cells}
	}
	spot := func(id string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: SleepingSpotDefinition, Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: owners, AccessibleTo: []PawnID{"a", "b"}, Cell: domain.Cell{X: 1, Z: 1}}
	}
	sleeping.Beds = []SleepingBed{spot("b1", "a"), spot("b2", "b")}
	rooms := RoomObservation{Rooms: []Room{{ID: "shell", Role: domain.Known(RoomRoleBarracks), Enclosed: domain.Known(true), Beds: []string{"b1", "b2"}, Cells: []domain.Cell{{X: 3, Z: 3}}}}}
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomShell || got.Room.Interior.X != 10 {
		t.Fatalf("spot owners = %+v, want the first bedroom shelled", got)
	}
	rooms.Rooms = append(rooms.Rooms, room("r1", 10))
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomFurnish {
		t.Fatalf("empty bedroom = %+v, want furnish", got)
	}
	rooms.Rooms[1] = room("r1", 10, "s1")
	sleeping.Beds = append(sleeping.Beds, spot("s1"))
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomMove || got.Pawn != "a" || got.Bed != "s1" {
		t.Fatalf("spot in the bedroom = %+v, want a moved in", got)
	}
	sleeping.People[0].OwnedBed = domain.Known("s1")
	sleeping.Beds[0].Owners, sleeping.Beds[2].Owners = nil, []PawnID{"a"}
	// One spot left in the shell: RimWorld reads it as a bedroom.
	rooms.Rooms[0].Role = domain.Known(RoomRoleBedroom)
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomClear || got.Bed != "b1" {
		t.Fatalf("vacated shell spot = %+v, want it cleared", got)
	}
	rooms.Rooms[0].Beds = []string{"b2"}
	sleeping.Beds = sleeping.Beds[1:]
	if got := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); got.Kind != BedroomShell || got.Room.Interior.X != 16 {
		t.Fatalf("last shell spot owner = %+v, want the second bedroom shelled", got)
	}
}

func TestBedResearchRequestedOnceEveryoneHasABedroom(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	sleeping.BedBuildable = domain.Known(false)
	// Both colonists still share the barracks.
	if got := BedResearchRequest(nil, plan, rooms, sleeping, nil); len(got) != 0 {
		t.Fatalf("barracks = %v, want no research", got)
	}
	rooms.Rooms = append(rooms.Rooms,
		Room{ID: "r1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"b1"}, Cells: []domain.Cell{{X: 12, Z: 2}}},
		Room{ID: "r2", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"b2"}, Cells: []domain.Cell{{X: 18, Z: 2}}})
	rooms.Rooms[0].Beds = []string{"b3"}
	// One colonist moved, the other still in the barracks.
	rooms.Rooms[2].Role = domain.Known(RoomRoleBarracks)
	if got := BedResearchRequest(nil, plan, rooms, sleeping, nil); len(got) != 0 {
		t.Fatalf("one still in barracks = %v, want no research", got)
	}
	rooms.Rooms[2].Role = domain.Known(RoomRoleBedroom)
	if got := BedResearchRequest([]string{"Stonecutting"}, plan, rooms, sleeping, nil); len(got) != 2 || got[1] != BedResearch {
		t.Fatalf("all in bedrooms = %v, want %s", got, BedResearch)
	}
	sleeping.BedBuildable = domain.Known(true)
	if got := BedResearchRequest(nil, plan, rooms, sleeping, nil); len(got) != 0 {
		t.Fatalf("bed available = %v, want no research", got)
	}
	sleeping.BedBuildable = domain.Unknown[bool]()
	if got := BedResearchRequest(nil, plan, rooms, sleeping, nil); len(got) != 0 {
		t.Fatalf("bed unknown = %v, want no research", got)
	}
}
