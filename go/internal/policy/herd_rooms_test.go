package policy

import (
	"strconv"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var testHerdFurniture = HerdFurniture{
	Spot: InteriorPieceDef{Def: AnimalSleepingSpotDefinition, Size: domain.Cell{X: 1, Z: 1}},
	Bed:  InteriorPieceDef{Def: AnimalBedDefinition, Size: domain.Cell{X: 1, Z: 1}},
}

func herdTestPlan(t *testing.T, animals int) LayoutPlan {
	t.Helper()
	core := PlanCore(utilityTestZones(), 3, BuildTierCamp)
	return PlanUtilities(core, UtilityWants{PenAnimals: animals})
}

// standing is the room census of rooms standing enclosed on each room's
// interior.
func standing(rooms ...LayoutRoom) RoomObservation {
	var out RoomObservation
	for i, r := range rooms {
		out.Rooms = append(out.Rooms, Room{ID: strconv.Itoa(i), Enclosed: domain.Known(true), Cells: rectCells(r.Interior)})
	}
	return out
}

func bedAt(t *testing.T, def string, p InteriorPiece) CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding(def, p.Anchor(), p.Rot, "")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: p.Slot, Building: b, Cells: rectCells(p.Rect)}
}

func TestHerdRoomsHaveADoorAndHoldTheirBeds(t *testing.T) {
	for _, animals := range []int{10, 20, 45} {
		plan := herdTestPlan(t, animals)
		barns, vets := plan.HerdRooms(ModuleBarn), plan.HerdRooms(ModuleVetRoom)
		if len(barns) != 1 || len(vets) != 1 {
			t.Fatal("want one barn and one vet room", animals, len(barns), len(vets))
		}
		for _, room := range append(barns, vets...) {
			if _, ok := doorSide(room.Interior, room.Door); !ok {
				t.Fatal("door is not in the room's ring", room)
			}
			if _, ok := InteriorRoomFromLayout(room, testShapes); !ok {
				t.Fatal("room has no interior plan input", room)
			}
		}
		if got := herdRoomBeds(barns[0], testShapes, testHerdFurniture.Spot); got < animals {
			t.Fatal("barn holds fewer sleeping spots than animals", animals, got)
		}
		if got := herdRoomBeds(vets[0], testShapes, testHerdFurniture.Bed); got < VetBeds(animals) || got < 2 {
			t.Fatal("vet room holds fewer beds than the herd needs", animals, got)
		}
	}
}

func TestHerdStepShellsThenFurnishesBarnThenVetRoom(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn, vet := plan.HerdRooms(ModuleBarn)[0], plan.HerdRooms(ModuleVetRoom)[0]
	const animals = 12
	step := NextHerdStep(plan, RoomObservation{Shapes: testShapes}, nil, nil, animals, testHerdFurniture)
	if step.Kind != HerdShell || step.Room != barn {
		t.Fatal("barn shell first", step)
	}
	var built []CurrentBuilding
	rooms := standing(barn)
	for i := 0; i < animals; i++ {
		step = NextHerdStep(plan, rooms, built, nil, animals, testHerdFurniture)
		if step.Kind != HerdPlace || step.Role != ModuleBarn || step.Piece.Def != AnimalSleepingSpotDefinition || !rectInside(barn.Interior, step.Piece.Rect) {
			t.Fatal("barn sleeping spot", i, step)
		}
		built = append(built, bedAt(t, step.Piece.Def, step.Piece))
	}
	step = NextHerdStep(plan, rooms, built, nil, animals, testHerdFurniture)
	if step.Kind != HerdShell || step.Room != vet {
		t.Fatal("vet room shell after the barn beds", step)
	}
	rooms = standing(barn, vet)
	for i := 0; i < VetBeds(animals); i++ {
		step = NextHerdStep(plan, rooms, built, nil, animals, testHerdFurniture)
		if step.Kind != HerdPlace || step.Role != ModuleVetRoom || step.Piece.Def != AnimalBedDefinition || !rectInside(vet.Interior, step.Piece.Rect) {
			t.Fatal("vet animal bed", i, step)
		}
		built = append(built, bedAt(t, step.Piece.Def, step.Piece))
	}
	if step = NextHerdStep(plan, rooms, built, nil, animals, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("every bed stands", step)
	}
	// The herd grows: the beds are topped up in the standing rooms.
	if step = NextHerdStep(plan, rooms, built, nil, animals+3, testHerdFurniture); step.Kind != HerdPlace || step.Role != ModuleBarn {
		t.Fatal("barn topped up", step)
	}
	if step = NextHerdStep(plan, rooms, nil, nil, 0, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("no animals owe no room", step)
	}
}

func TestHerdStepFlagsEachStandingVetBedMedical(t *testing.T) {
	plan := herdTestPlan(t, 20)
	vet := plan.HerdRooms(ModuleVetRoom)[0]
	layout, ok := PlanInterior(mustInterior(t, vet), testHerdFurniture.Bed)
	if !ok {
		t.Fatal("no vet layout")
	}
	bed := bedAt(t, AnimalBedDefinition, layout.Pieces[0])
	census := func(medical domain.Fact[bool]) []SleepingBed {
		return []SleepingBed{{ID: bed.ID, Medical: medical}}
	}
	// Without a barn, the vet room's own step is the first one read.
	only := plan
	only.Reservations = nil
	for _, r := range plan.Reservations {
		if r.Kind != ReserveBarn {
			only.Reservations = append(only.Reservations, r)
		}
	}
	step := NextHerdStep(only, standing(vet), []CurrentBuilding{bed}, census(domain.Known(false)), 1, testHerdFurniture)
	if step.Kind != HerdMedical || step.Bed != bed.ID {
		t.Fatal("flag the standing vet bed", step)
	}
	for _, medical := range []domain.Fact[bool]{domain.Known(true), domain.Unknown[bool]()} {
		if step = NextHerdStep(only, standing(vet), []CurrentBuilding{bed}, census(medical), 1, testHerdFurniture); step.Kind == HerdMedical {
			t.Fatal("a flagged or unread bed is not flagged again", step)
		}
	}
}

func TestHerdOutgrowsItsRoomsAndAddsAnotherWithoutMovingAny(t *testing.T) {
	small := herdTestPlan(t, 10)
	grown := PlanHerdSites(small, 60)
	if len(grown.HerdRooms(ModuleBarn)) < 2 {
		t.Fatal("no second barn for a herd of 60", len(grown.HerdRooms(ModuleBarn)))
	}
	if grown.herdCapacity(ModuleBarn) < 60 || grown.herdCapacity(ModuleVetRoom) < VetBeds(60) {
		t.Fatal("rooms hold fewer beds than the herd", grown.herdCapacity(ModuleBarn), grown.herdCapacity(ModuleVetRoom))
	}
	for i, r := range small.Reservations {
		if grown.Reservations[i] != r {
			t.Fatal("a placed reservation moved", r)
		}
	}
	if again := PlanHerdSites(grown, 60); len(again.Reservations) != len(grown.Reservations) {
		t.Fatal("top-up is not idempotent")
	}
	// The second barn is furnished once the first holds its share.
	first, second := grown.HerdRooms(ModuleBarn)[0], grown.HerdRooms(ModuleBarn)[1]
	var built []CurrentBuilding
	rooms := standing(first)
	layout, ok := PlanInterior(mustInterior(t, first), testHerdFurniture.Spot)
	if !ok {
		t.Fatal("no barn layout")
	}
	for _, p := range layout.Pieces {
		built = append(built, bedAt(t, p.Def, p))
	}
	step := NextHerdStep(grown, rooms, built, nil, 60, testHerdFurniture)
	if step.Kind != HerdShell || step.Room != second {
		t.Fatal("second barn shell once the first is full", step)
	}
}

func mustInterior(t *testing.T, r LayoutRoom) InteriorRoom {
	t.Helper()
	in, ok := InteriorRoomFromLayout(r, testShapes)
	if !ok {
		t.Fatal("no interior", r)
	}
	return in
}

func TestBarnAndVetRoomAreAnimalFurnitureRooms(t *testing.T) {
	f, err := Facility(RoomRoleBarn)
	if err != nil || f.Status != FacilityImplemented {
		t.Fatal("barn is an implemented facility", f, err)
	}
	if _, ok := InteriorTemplateFor(RoomRoleVetRoom); !ok {
		t.Fatal("vet room has a template")
	}
}
