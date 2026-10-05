package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// shelterRetireFixture is a derived plan with every planned room standing
// and each colonist owning a bed in a bedroom: the shelter is retirable until
// a test breaks one condition.
type shelterRetireFixture struct {
	plan     LayoutPlan
	survey   MapSurvey
	rooms    RoomObservation
	sleeping SleepingObservation
	shelter  PlannedRoom
}

func newShelterRetireFixture(t *testing.T) shelterRetireFixture {
	t.Helper()
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, 2, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	// The laboratory is demand-grown: a replan asked for it sites it.
	plan, _, _ = ReplanLayoutWithRooms(plan, s, RoomGrowth{Core: []PlannedRole{PlannedLab}}, 0, 2, 0, BuildTierCamp, nil, nil)
	f := shelterRetireFixture{plan: plan, survey: s}
	if shelters := plan.roomsOf(PlannedShelter); len(shelters) == 1 {
		f.shelter = shelters[0]
	} else {
		t.Fatal("shelters", len(shelters))
	}
	for _, role := range []PlannedRole{PlannedWorkshop, PlannedLab} {
		if len(plan.roomsOf(role)) == 0 {
			t.Skip("plan has no", role)
		}
	}
	standing := func(id string, r PlannedRoom, role RoomRole, beds ...string) Room {
		var cells []domain.Cell
		cells = append(cells, rectCells(r.Interior)...)
		return Room{ID: id, Role: domain.Known(role), Enclosed: domain.Known(true), Cells: cells, Beds: beds}
	}
	f.rooms.Rooms = append(f.rooms.Rooms, standing("shelter", f.shelter, RoomRoleBedroom, "spot1", "spot2"))
	for i, role := range []PlannedRole{PlannedWorkshop, PlannedLab} {
		for j, r := range plan.roomsOf(role) {
			f.rooms.Rooms = append(f.rooms.Rooms, standing(string(role)+string(rune('a'+i*4+j)), r, RoomRoleWorkshop))
		}
	}
	bedrooms := plan.roomsOf(PlannedBedroom)
	if len(bedrooms) < 2 {
		t.Skip("plan has too few bedrooms")
	}
	f.rooms.Rooms = append(f.rooms.Rooms,
		standing("bedroom1", bedrooms[0], RoomRoleBedroom, "bed1"),
		standing("bedroom2", bedrooms[1], RoomRoleBedroom, "bed2"))
	f.sleeping = SleepingObservation{Colonists: 2, People: []SleepingPerson{
		{ID: "a", OwnedBed: domain.Known("bed1")},
		{ID: "b", OwnedBed: domain.Known("bed2")},
	}}
	return f
}

func (f shelterRetireFixture) replan(t *testing.T, retire bool, inFlight map[Rectangle]bool) (LayoutPlan, bool) {
	t.Helper()
	growth := RoomGrowth{RetireShelter: retire, InFlight: inFlight}
	next, changed, _ := ReplanLayoutWithRooms(f.plan, f.survey, growth, 0, 2, 0, BuildTierCamp, nil, nil)
	return next, changed
}

// The shelter retires through the real replan only when every colonist owns
// a bed in a built bedroom and the workshop and laboratory stand (#2046).
func TestShelterRetiresWhenBedroomsWorkshopAndLabStand(t *testing.T) {
	f := newShelterRetireFixture(t)
	if !ShelterRetirable(f.plan, f.rooms, f.sleeping) {
		t.Fatal("a housed colony with workshop and lab standing is not retirable")
	}
	next, changed := f.replan(t, true, nil)
	if !changed || len(next.roomsOf(PlannedShelter)) != 0 {
		t.Fatalf("changed=%v shelters=%d", changed, len(next.roomsOf(PlannedShelter)))
	}
	if len(next.roomsOf(PlannedWorkshop)) == 0 || len(next.roomsOf(PlannedLab)) == 0 || len(next.roomsOf(PlannedBedroom)) < 2 {
		t.Fatal("retirement dropped a room it should keep")
	}
	// The gate was not met: the replan leaves the shelter alone.
	if kept, _ := f.replan(t, false, nil); len(kept.roomsOf(PlannedShelter)) != 1 {
		t.Fatal("the shelter left the plan without retirement")
	}
}

func TestShelterRetirementGates(t *testing.T) {
	f := newShelterRetireFixture(t)
	t.Run("joiner without a bed", func(t *testing.T) {
		s := f.sleeping
		s.Colonists = 3
		s.People = append(append([]SleepingPerson(nil), s.People...), SleepingPerson{ID: "c", OwnedBed: domain.Known("")})
		if ShelterRetirable(f.plan, f.rooms, s) {
			t.Fatal("retirable with a bedless colonist")
		}
	})
	t.Run("bed only in the shelter", func(t *testing.T) {
		s := f.sleeping
		s.People = []SleepingPerson{{ID: "a", OwnedBed: domain.Known("spot1")}, {ID: "b", OwnedBed: domain.Known("bed2")}}
		if ShelterRetirable(f.plan, f.rooms, s) {
			t.Fatal("a shelter spot counted as a built bedroom bed")
		}
	})
	t.Run("unknown bed", func(t *testing.T) {
		s := f.sleeping
		s.People = []SleepingPerson{{ID: "a", OwnedBed: domain.Unknown[string]()}, {ID: "b", OwnedBed: domain.Known("bed2")}}
		if ShelterRetirable(f.plan, f.rooms, s) {
			t.Fatal("retirable on an unknown bed")
		}
	})
	t.Run("workshop and lab must stand", func(t *testing.T) {
		for _, role := range []PlannedRole{PlannedWorkshop, PlannedLab} {
			for _, r := range f.plan.roomsOf(role) {
				var kept []Room
				for _, room := range f.rooms.Rooms {
					if !containsCell(room.Cells, domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}) {
						kept = append(kept, room)
					}
				}
				rooms := f.rooms
				rooms.Rooms = kept
				if ShelterRetirable(f.plan, rooms, f.sleeping) {
					t.Fatalf("retirable with a %s not standing", role)
				}
			}
		}
	})
	t.Run("work in flight", func(t *testing.T) {
		next, changed := f.replan(t, true, map[Rectangle]bool{f.shelter.Interior: true})
		if len(next.roomsOf(PlannedShelter)) != 1 {
			t.Fatalf("a shelter with open work was dropped (changed=%v)", changed)
		}
	})
}

func containsCell(cells []domain.Cell, c domain.Cell) bool {
	for _, x := range cells {
		if x == c {
			return true
		}
	}
	return false
}

func TestInFlightRoomsKeysByInteriorOrigin(t *testing.T) {
	a := PlannedRoom{Role: PlannedShelter, Interior: Rectangle{X: 4, Z: 5, Width: 5, Height: 4}}
	b := PlannedRoom{Role: PlannedLab, Interior: Rectangle{X: 20, Z: 5, Width: 5, Height: 4}}
	got := InFlightRooms(LayoutPlan{Rooms: []PlannedRoom{a, b}}, map[domain.Cell]bool{{X: 4, Z: 5}: true})
	if len(got) != 1 || !got[a.Interior] {
		t.Fatal(got)
	}
}

// The retired shelter's footprint is recorded by the real replan and survives
// the next one; clearance then takes the furniture, the walls and the floor
// down, and the entry is dropped once the
// ground is clear (#2075).
func TestRetiredShelterGroundIsDemolishedThenDropped(t *testing.T) {
	f := newShelterRetireFixture(t)
	next, _ := f.replan(t, true, nil)
	ground := roomGround(f.shelter.Interior)
	if len(next.RetiredGround) != 1 || next.RetiredGround[0] != ground || !next.Valid() {
		t.Fatalf("retired ground %v, want %v", next.RetiredGround, ground)
	}
	again, _, _ := ReplanLayoutWithRooms(next, f.survey, RoomGrowth{}, 0, 2, 0, BuildTierCamp, nil, nil)
	if len(again.RetiredGround) != 1 {
		t.Fatalf("a later replan lost the entry: %v", again.RetiredGround)
	}
	in := f.shelter.Interior
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
	keeper.Rooms = append(append([]PlannedRoom(nil), next.Rooms...), PlannedRoom{Role: PlannedReserve, Interior: Rectangle{X: in.X - 4, Z: in.Z, Width: 3, Height: 3}, DoorRot: domain.North})
	heldWall := playerRow("held", "Wall", "ancient_wall_door", shared, shared, true)
	if work := PlannedGroundWork(keeper, GroundCensus{}, []ClearanceTarget{heldWall}, nil, rooms, RetiredGroundOf(keeper), wantsAll); len(work) != 0 {
		t.Fatalf("a kept room's wall is demolition work: %v", work)
	}
}
