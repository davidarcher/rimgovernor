package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func sleepingPerson(id PawnID, lo, hi float64) SleepingPerson {
	return SleepingPerson{ID: id, ComfortableMin: domain.Known(lo), ComfortableMax: domain.Known(hi)}
}

func sleepingRoom(id string, role RoomRole, temperature float64, cells ...domain.Cell) Room {
	return Room{ID: id, Role: domain.Known(role), Enclosed: domain.Known(true), Temperature: domain.Known(temperature), Cells: cells}
}

func sleepingBedDefinition(available bool) []BenchDefinition {
	return []BenchDefinition{{Name: "Bed", Available: domain.Known(available)}}
}

func TestBedroomIsHostedByBarracks(t *testing.T) {
	f, err := Facility(RoomRoleBedroom)
	if err != nil || f.Status != FacilityImplemented {
		t.Fatal(f, err)
	}
	for _, role := range []RoomRole{RoomRoleBedroom, RoomRoleBarracks, RoomRoleRoom} {
		if !f.Hosts(role) {
			t.Fatal("expected hosting role", role)
		}
	}
	if f.Hosts(RoomRoleKitchen) || f.Hosts(RoomRoleHospital) {
		t.Fatal("kitchen or hospital hosts a sleeping bed")
	}
}

func TestSelectSleepingMethodUnknownAndNoDemand(t *testing.T) {
	choice, err := SelectSleepingMethod(SleepingRequest{})
	if err != nil || choice.Method != SleepingUnknown {
		t.Fatal(choice, err)
	}
	choice, err = SelectSleepingMethod(SleepingRequest{Targets: domain.Known([]SleepingTarget{})})
	if err != nil || choice.Method != SleepingNoDemand || choice.Waiting != 0 {
		t.Fatal(choice, err)
	}
	// Everyone waiting owns a suitable bed: only observed use completes it.
	choice, err = SelectSleepingMethod(SleepingRequest{Targets: domain.Known([]SleepingTarget{{Pawn: "p1", Kind: SleepingUseNeeded, PreviousBed: "bed-1"}}), Sleeping: domain.Known(SleepingObservation{})})
	if err != nil || choice.Method != SleepingNoDemand || choice.Waiting != 1 || choice.Unhoused != 0 {
		t.Fatal(choice, err)
	}
}

func TestSelectSleepingMethodAssignsLowestPawnAndBed(t *testing.T) {
	targets := []SleepingTarget{
		{Pawn: "p2", Kind: SleepingUnsafe, PreviousBed: "spot-2", Available: []string{"bed-9", "bed-3"}},
		{Pawn: "p1", Kind: SleepingUpgrade, PreviousBed: "spot-1", Available: []string{"bed-7", "bed-5"}},
		{Pawn: "p0", Kind: SleepingUpgrade, PreviousBed: "spot-0"},
	}
	choice, err := SelectSleepingMethod(SleepingRequest{Targets: domain.Known(targets)})
	// Available is the review's preference order, taken as given.
	if err != nil || choice.Method != SleepingAssign || choice.Pawn != "p1" || choice.Bed != "bed-7" || choice.PreviousBed != "spot-1" || choice.Waiting != 3 {
		t.Fatal(choice, err)
	}
	// A bed the pawn already owns is never reassigned to itself.
	choice, err = SelectSleepingMethod(SleepingRequest{Targets: domain.Known([]SleepingTarget{{Pawn: "p1", Kind: SleepingUpgrade, PreviousBed: "bed-1", Available: []string{"bed-1", "bed-2"}}})})
	if err != nil || choice.Method != SleepingAssign || choice.Bed != "bed-2" {
		t.Fatal(choice, err)
	}
}

func TestSelectSleepingMethodBuildsInRoomWithinComfortBand(t *testing.T) {
	targets := domain.Known([]SleepingTarget{
		{Pawn: "p1", Kind: SleepingUpgrade, PreviousBed: "spot-1"},
		{Pawn: "p2", Kind: SleepingUnsafe},
		{Pawn: "p3", Kind: SleepingUseNeeded, PreviousBed: "bed-3"},
	})
	sleeping := domain.Known(SleepingObservation{People: []SleepingPerson{sleepingPerson("p1", 10, 30), sleepingPerson("p2", 16, 26), sleepingPerson("p3", 0, 40)}})
	warm := domain.Cell{X: 1, Z: 1}
	rooms := domain.Known(RoomObservation{Shapes: testShapes, Rooms: []Room{
		sleepingRoom("cold", RoomRoleBarracks, 12, domain.Cell{X: 5, Z: 5}),
		sleepingRoom("warm", RoomRoleBedroom, 20, warm),
		sleepingRoom("kitchen", RoomRoleKitchen, 21, domain.Cell{X: 9, Z: 9}),
	}})
	choice, err := SelectSleepingMethod(SleepingRequest{Targets: targets, Sleeping: sleeping, Rooms: rooms, Definitions: sleepingBedDefinition(true)})
	if err != nil || choice.Method != SleepingBuild || choice.Definition != "Bed" || choice.Waiting != 3 || choice.Unhoused != 2 {
		t.Fatal(choice, err)
	}
	if len(choice.Cells) != 1 || choice.Cells[0] != warm {
		t.Fatal("expected only the warm bedroom's cells", choice.Cells)
	}
	// No room in band: the build has no site, but the method is still Build.
	rooms = domain.Known(RoomObservation{Shapes: testShapes, Rooms: []Room{sleepingRoom("cold", RoomRoleBarracks, 12)}})
	choice, err = SelectSleepingMethod(SleepingRequest{Targets: targets, Sleeping: sleeping, Rooms: rooms, Definitions: sleepingBedDefinition(true)})
	if err != nil || choice.Method != SleepingBuild || len(choice.Cells) != 0 {
		t.Fatal(choice, err)
	}
	// Bed not buildable and the ladder's lower rungs unread: unknown;
	// no definitions at all: unknown.
	choice, err = SelectSleepingMethod(SleepingRequest{Targets: targets, Sleeping: sleeping, Rooms: rooms, Definitions: sleepingBedDefinition(false)})
	if err != nil || choice.Method != SleepingUnknown {
		t.Fatal(choice, err)
	}
	choice, err = SelectSleepingMethod(SleepingRequest{Targets: targets, Sleeping: sleeping, Rooms: rooms})
	if err != nil || choice.Method != SleepingUnknown {
		t.Fatal(choice, err)
	}
	// An unhoused pawn without a known comfort band blocks the choice.
	unknownBand := domain.Known(SleepingObservation{People: []SleepingPerson{sleepingPerson("p1", 10, 30)}})
	choice, err = SelectSleepingMethod(SleepingRequest{Targets: targets, Sleeping: unknownBand, Rooms: rooms, Definitions: sleepingBedDefinition(true)})
	if err != nil || choice.Method != SleepingUnknown {
		t.Fatal(choice, err)
	}
}

// The bed ladder (#1181): Bed, then a bedroll with its stuff on hand, then a
// sleeping spot; a couple's double of each rung first.
func TestSelectSleepingMethodBedLadder(t *testing.T) {
	targets := domain.Known([]SleepingTarget{{Pawn: "p1", Kind: SleepingUpgrade}})
	sleeping := domain.Known(SleepingObservation{People: []SleepingPerson{sleepingPerson("p1", 10, 30)}})
	rooms := domain.Known(RoomObservation{Shapes: testShapes, Rooms: []Room{sleepingRoom("r", RoomRoleBedroom, 20, domain.Cell{X: 1, Z: 1})}})
	ladder := func(bed bool) []BenchDefinition {
		return []BenchDefinition{
			{Name: "Bed", Available: domain.Known(bed)}, {Name: "DoubleBed", Available: domain.Known(bed)},
			{Name: "Bedroll", Available: domain.Known(true)}, {Name: "BedrollDouble", Available: domain.Known(true)},
			{Name: "SleepingSpot", Available: domain.Known(true)},
		}
	}
	for _, c := range []struct {
		name    string
		bed     bool
		stocked map[string]bool
		couple  bool
		want    string
	}{
		{"bed available", true, map[string]bool{"Bedroll": true}, false, "Bed"},
		{"bed locked, cloth or leather on hand", false, map[string]bool{"Bedroll": true, "BedrollDouble": true}, false, "Bedroll"},
		{"bed locked, no stuff", false, nil, false, ""},
		{"couple, bed available", true, nil, true, "DoubleBed"},
		{"couple, bed locked, stuff on hand", false, map[string]bool{"Bedroll": true, "BedrollDouble": true}, true, "BedrollDouble"},
	} {
		tg := targets
		if c.couple {
			tg = domain.Known([]SleepingTarget{{Pawn: "p1", Kind: SleepingUpgrade, Partner: "p2"}})
		}
		choice, err := SelectSleepingMethod(SleepingRequest{Targets: tg, Sleeping: sleeping, Rooms: rooms, Definitions: ladder(c.bed), Stocked: c.stocked})
		if want := map[bool]SleepingMethod{true: SleepingBuild, false: SleepingUnavailable}[c.want != ""]; err != nil || choice.Method != want || choice.Definition != c.want {
			t.Fatal(c.name, choice, err)
		}
	}
}
