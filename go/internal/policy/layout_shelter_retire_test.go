package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// shelterRetireFixture is a plan with every planned room standing and each
// colonist owning a bed in a bedroom: the shelter is retirable until a test
// breaks one condition.
type shelterRetireFixture struct {
	plan     LayoutPlan
	rooms    RoomObservation
	sleeping SleepingObservation
	shelter  PlannedRoom
}

func newShelterRetireFixture(t *testing.T) shelterRetireFixture {
	t.Helper()
	at := func(id int32, role PlannedRole, x int32) PlannedRoom {
		return PlannedRoom{ID: id, Role: role, Region: RectRegion(Rectangle{X: x, Z: 0, Width: 5, Height: 4}), DoorRot: domain.North}
	}
	plan := LayoutPlan{Rooms: []PlannedRoom{
		at(1, PlannedShelter, 0), at(2, PlannedWorkshop, 10), at(3, PlannedLab, 20),
		at(4, PlannedBedroom, 30), at(5, PlannedBedroom, 40),
	}}
	f := shelterRetireFixture{plan: plan, shelter: plan.Rooms[0]}
	standing := func(id string, r PlannedRoom, role RoomRole, beds ...string) Room {
		return Room{ID: id, Role: domain.Known(role), Enclosed: domain.Known(true), Cells: rectCells(r.Region.Bounds()), Beds: beds}
	}
	f.rooms.Rooms = []Room{
		standing("shelter", plan.Rooms[0], RoomRoleBedroom, "spot1", "spot2"),
		standing("workshop", plan.Rooms[1], RoomRoleWorkshop),
		standing("lab", plan.Rooms[2], RoomRoleWorkshop),
		standing("bedroom1", plan.Rooms[3], RoomRoleBedroom, "bed1"),
		standing("bedroom2", plan.Rooms[4], RoomRoleBedroom, "bed2"),
	}
	f.sleeping = SleepingObservation{Colonists: 2, People: []SleepingPerson{
		{ID: "a", OwnedBed: domain.Known("bed1")},
		{ID: "b", OwnedBed: domain.Known("bed2")},
	}}
	return f
}

// The shelter is retirable only when every colonist owns a bed in a built
// bedroom and the workshop and laboratory stand.
func TestShelterRetirementGates(t *testing.T) {
	f := newShelterRetireFixture(t)
	if !ShelterRetirable(f.plan, f.rooms, f.sleeping, nil) {
		t.Fatal("a housed colony with workshop and lab standing is not retirable")
	}
	t.Run("joiner without a bed", func(t *testing.T) {
		s := f.sleeping
		s.Colonists = 3
		s.People = append(append([]SleepingPerson(nil), s.People...), SleepingPerson{ID: "c", OwnedBed: domain.Known("")})
		if ShelterRetirable(f.plan, f.rooms, s, nil) {
			t.Fatal("retirable with a bedless colonist")
		}
	})
	t.Run("bed only in the shelter", func(t *testing.T) {
		s := f.sleeping
		s.People = []SleepingPerson{{ID: "a", OwnedBed: domain.Known("spot1")}, {ID: "b", OwnedBed: domain.Known("bed2")}}
		if ShelterRetirable(f.plan, f.rooms, s, nil) {
			t.Fatal("a shelter spot counted as a built bedroom bed")
		}
	})
	t.Run("unknown bed", func(t *testing.T) {
		s := f.sleeping
		s.People = []SleepingPerson{{ID: "a", OwnedBed: domain.Unknown[string]()}, {ID: "b", OwnedBed: domain.Known("bed2")}}
		if ShelterRetirable(f.plan, f.rooms, s, nil) {
			t.Fatal("retirable on an unknown bed")
		}
	})
	t.Run("workshop and lab must stand", func(t *testing.T) {
		for _, role := range []PlannedRole{PlannedWorkshop, PlannedLab} {
			for _, r := range f.plan.roomsOf(role) {
				var kept []Room
				for _, room := range f.rooms.Rooms {
					if !containsCell(room.Cells, domain.Cell{X: r.Region.Bounds().X + r.Region.Bounds().Width/2, Z: r.Region.Bounds().Z + r.Region.Bounds().Height/2}) {
						kept = append(kept, room)
					}
				}
				rooms := f.rooms
				rooms.Rooms = kept
				if ShelterRetirable(f.plan, rooms, f.sleeping, nil) {
					t.Fatalf("retirable with a %s not standing", role)
				}
			}
		}
	})
}

// A solo colony plans no laboratory room: its shelter retires once the
// colonist sleeps in a built bedroom, a workshop stands and the research bench
// stands in a built room outside the shelter, and stays otherwise.
func TestShelterRetiresForSoloColonyWithBenchInBedroom(t *testing.T) {
	shelter := PlannedRoom{Role: PlannedShelter, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 5, Height: 4})}
	workshop := PlannedRoom{Role: PlannedWorkshop, Region: RectRegion(Rectangle{X: 10, Z: 0, Width: 5, Height: 4})}
	bedroom := PlannedRoom{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 20, Z: 0, Width: 5, Height: 4})}
	plan := LayoutPlan{Rooms: []PlannedRoom{shelter, workshop, bedroom}}
	room := func(id string, r PlannedRoom, role RoomRole, beds ...string) Room {
		return Room{ID: id, Role: domain.Known(role), Enclosed: domain.Known(true), Cells: rectCells(r.Region.Bounds()), Beds: beds}
	}
	rooms := RoomObservation{Rooms: []Room{
		room("s", shelter, RoomRoleBedroom, "spot"),
		room("w", workshop, RoomRoleWorkshop),
		room("b", bedroom, RoomRoleBedroom, "bed"),
	}}
	sleeping := SleepingObservation{Colonists: 1, People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("bed")}}}
	bench, err := domain.NewBuilding("SimpleResearchBench", domain.Cell{X: 21, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	inBedroom := []CurrentBuilding{{ID: "t", Building: bench, Cells: []domain.Cell{{X: 21, Z: 1}, {X: 22, Z: 1}}}}
	if !ShelterRetirable(plan, rooms, sleeping, inBedroom) {
		t.Fatal("solo colony with its bench in the bedroom keeps the shelter")
	}
	inShelter := []CurrentBuilding{{ID: "t", Building: bench, Cells: []domain.Cell{{X: 1, Z: 1}}}}
	if ShelterRetirable(plan, rooms, sleeping, inShelter) {
		t.Fatal("retired while the bench still stands in the shelter")
	}
	if ShelterRetirable(plan, rooms, sleeping, nil) {
		t.Fatal("retired with no bench outside the shelter")
	}
	cold := plan
	cold.Cold = true
	if ShelterRetirable(cold, rooms, sleeping, inBedroom) {
		t.Fatal("cold map retired into a bedroom that holds no temperature")
	}
	rooms.Rooms[2].TemperatureControl = domain.Known(true)
	if !ShelterRetirable(cold, rooms, sleeping, inBedroom) {
		t.Fatal("cold map with a heated bedroom keeps the shelter")
	}
	if ShelterRetirable(LayoutPlan{Rooms: []PlannedRoom{shelter}}, RoomObservation{Rooms: rooms.Rooms[:1]}, sleeping, nil) {
		t.Fatal("nothing else built must keep the shelter")
	}
}

func containsCell(cells []domain.Cell, c domain.Cell) bool {
	for _, x := range cells {
		if x == c {
			return true
		}
	}
	return false
}

// A standing room the next plan no longer holds has its ground recorded
// (retiredGround) and kept by a later replan; clearance then takes the
// furniture, the walls and the floor down, and the entry is dropped once the
// ground is clear.
func TestRetiredShelterGroundIsDemolishedThenDropped(t *testing.T) {
	f := newShelterRetireFixture(t)
	kept := LayoutPlan{Rooms: f.plan.Rooms[1:]}
	next := kept
	next.RetiredGround = retiredGround(f.plan, kept, map[int32]bool{f.shelter.ID: true})
	ground := roomGround(f.shelter.Region.Bounds())
	if len(next.RetiredGround) != 1 || !next.RetiredGround[0].Equal(RectRegion(ground)) {
		t.Fatalf("retired ground %v, want %v", next.RetiredGround, ground)
	}
	if again := retiredGround(next, kept, nil); len(again) != 1 {
		t.Fatalf("a later replan lost the entry: %v", again)
	}
	in := f.shelter.Region.Bounds()
	spot := playerRow("spot", "SleepingSpot", "other", domain.Cell{X: in.X, Z: in.Z}, domain.Cell{X: in.X, Z: in.Z + 1}, false)
	craft := playerRow("craft", "CraftingSpot", "other", domain.Cell{X: in.X + 1, Z: in.Z}, domain.Cell{X: in.X + 1, Z: in.Z}, false)
	table := playerRow("table", "SimpleResearchBench", "other", domain.Cell{X: in.X + 2, Z: in.Z}, domain.Cell{X: in.X + 3, Z: in.Z + 1}, false)
	wall := playerRow("wall", "Wall", "ancient_wall_door", domain.Cell{X: ground.X, Z: ground.Z}, domain.Cell{X: ground.X, Z: ground.Z}, true)
	floors := []ClearanceFloor{{Cell: domain.Cell{X: in.X, Z: in.Z + 2}, DefName: "WoodPlankFloor"}}
	rooms := RoomObservation{Shapes: testShapes}
	rg := RetiredGroundOf(next)
	stepOf := func(rows ...ClearanceTarget) (GroundStep, bool) {
		return PlannedGroundStep(next, GroundCensus{}, rows, floors, rooms, rg, wantsAll)
	}

	// Furniture (the shelter spots and the table included) first, then walls, then floors.
	step, ok := stepOf(spot, craft, table, wall)
	if !ok || step.Phase != GroundFurniture || len(step.Targets) != 3 {
		t.Fatalf("furniture first: %+v", step)
	}
	if step, ok = stepOf(wall); !ok || step.Phase != GroundWalls || step.Targets[0].EntityID != "wall" {
		t.Fatalf("walls second: %+v", step)
	}
	if step, ok = stepOf(); !ok || step.Phase != GroundFloors || len(step.Floors) != 1 {
		t.Fatalf("floors last: %+v", step)
	}
	if done := RetiredGroundDone(next, []ClearanceTarget{wall}, floors); len(done) != 0 {
		t.Fatalf("dropped before the ground was clear: %v", done)
	}
	done := RetiredGroundDone(next, nil, nil)
	if len(done) != 1 || len(next.WithoutRetiredGround(done).RetiredGround) != 0 || len(next.RetiredGround) != 1 {
		t.Fatalf("done %v", done)
	}
	// A wall a kept room shares with the retired ring stays.
	shared := domain.Cell{X: in.X - 1, Z: in.Z}
	keeper := next
	keeper.Rooms = append(append([]PlannedRoom(nil), next.Rooms...), PlannedRoom{Role: PlannedReserve, Region: RectRegion(Rectangle{X: in.X - 4, Z: in.Z, Width: 3, Height: 3}), DoorRot: domain.North})
	heldWall := playerRow("held", "Wall", "ancient_wall_door", shared, shared, true)
	if work := PlannedGroundWork(keeper, GroundCensus{}, []ClearanceTarget{heldWall}, nil, rooms, RetiredGroundOf(keeper), wantsAll); len(work) != 0 {
		t.Fatalf("a kept room's wall is demolition work: %v", work)
	}
}
