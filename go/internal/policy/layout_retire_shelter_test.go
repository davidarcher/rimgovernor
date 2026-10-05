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
	shelter  LayoutRoom
}

func newShelterRetireFixture(t *testing.T) shelterRetireFixture {
	t.Helper()
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, 2, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	// The laboratory is demand-grown: a replan asked for it sites it.
	plan, _, _ = ReplanLayoutWithRooms(plan, s, RoomGrowth{Core: []ModuleRole{ModuleLab}}, 0, 2, 0, BuildTierCamp, nil, nil)
	f := shelterRetireFixture{plan: plan, survey: s}
	if shelters := plan.roomsOf(ModuleShelter); len(shelters) == 1 {
		f.shelter = shelters[0]
	} else {
		t.Fatal("shelters", len(shelters))
	}
	for _, role := range []ModuleRole{ModuleWorkshop, ModuleLab} {
		if len(plan.roomsOf(role)) == 0 {
			t.Skip("plan has no", role)
		}
	}
	standing := func(id string, r LayoutRoom, role RoomRole, beds ...string) Room {
		var cells []domain.Cell
		cells = append(cells, rectCells(r.Interior)...)
		return Room{ID: id, Role: domain.Known(role), Enclosed: domain.Known(true), Cells: cells, Beds: beds}
	}
	f.rooms.Rooms = append(f.rooms.Rooms, standing("shelter", f.shelter, RoomRoleBedroom, "spot1", "spot2"))
	for i, role := range []ModuleRole{ModuleWorkshop, ModuleLab} {
		for j, r := range plan.roomsOf(role) {
			f.rooms.Rooms = append(f.rooms.Rooms, standing(string(role)+string(rune('a'+i*4+j)), r, RoomRoleWorkshop))
		}
	}
	bedrooms := plan.roomsOf(ModuleBedroom)
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
	if !ShelterRetirable(f.plan, f.rooms, f.sleeping, nil) {
		t.Fatal("a housed colony with workshop and lab standing is not retirable")
	}
	next, changed := f.replan(t, true, nil)
	if !changed || len(next.roomsOf(ModuleShelter)) != 0 {
		t.Fatalf("changed=%v shelters=%d", changed, len(next.roomsOf(ModuleShelter)))
	}
	if len(next.roomsOf(ModuleWorkshop)) == 0 || len(next.roomsOf(ModuleLab)) == 0 || len(next.roomsOf(ModuleBedroom)) < 2 {
		t.Fatal("retirement dropped a room it should keep")
	}
	// The gate was not met: the replan leaves the shelter alone.
	if kept, _ := f.replan(t, false, nil); len(kept.roomsOf(ModuleShelter)) != 1 {
		t.Fatal("the shelter left the plan without retirement")
	}
}

func TestShelterRetirementGates(t *testing.T) {
	f := newShelterRetireFixture(t)
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
		for _, role := range []ModuleRole{ModuleWorkshop, ModuleLab} {
			for _, r := range f.plan.roomsOf(role) {
				var kept []Room
				for _, room := range f.rooms.Rooms {
					if !containsCell(room.Cells, domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}) {
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
	t.Run("research table still in the shelter", func(t *testing.T) {
		in := f.shelter.Interior
		b, err := domain.NewBuilding("SimpleResearchBench", domain.Cell{X: in.X, Z: in.Z}, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		table := CurrentBuilding{ID: "bench", Building: b, Cells: rectCells(Rectangle{X: in.X, Z: in.Z, Width: 3, Height: 2})}
		if ShelterRetirable(f.plan, f.rooms, f.sleeping, []CurrentBuilding{table}) {
			t.Fatal("retirable with the research table in the shelter")
		}
	})
	t.Run("work in flight", func(t *testing.T) {
		next, changed := f.replan(t, true, map[Rectangle]bool{f.shelter.Interior: true})
		if len(next.roomsOf(ModuleShelter)) != 1 {
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
	a := LayoutRoom{Role: ModuleShelter, Interior: Rectangle{X: 4, Z: 5, Width: 5, Height: 4}}
	b := LayoutRoom{Role: ModuleLab, Interior: Rectangle{X: 20, Z: 5, Width: 5, Height: 4}}
	got := InFlightRooms(LayoutPlan{Rooms: []LayoutRoom{a, b}}, map[domain.Cell]bool{{X: 4, Z: 5}: true})
	if len(got) != 1 || !got[a.Interior] {
		t.Fatal(got)
	}
}

// The retired shelter's footprint is recorded by the real replan and survives
// the next one; clearance then takes the furniture, the walls and the floor
// down, waits on a standing research table, and the entry is dropped once the
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
	grounds := PlannedGround(next, rooms)
	rg := RetiredGroundOf(next)
	stepOf := func(rows ...ClearanceTarget) (GroundStep, bool) {
		return PlannedGroundStep(rows, floors, grounds, PlannedDoors(next), rooms, rg)
	}

	// The table holds the whole ground: nothing comes down and the entry stays.
	if step, ok := stepOf(spot, craft, table, wall); ok && step.Ground == ground {
		t.Fatalf("clearance moved on a standing research table: %+v", step)
	}
	if work := PlannedGroundWork([]ClearanceTarget{spot, craft, table, wall}, floors, grounds, PlannedDoors(next), rg); len(work) != 0 {
		t.Fatalf("a waiting ground owes work: %v", work)
	}
	if len(RetiredGroundDone(next, []ClearanceTarget{table}, nil)) != 0 {
		t.Fatal("the entry dropped under a standing table")
	}
	// Table relocated: furniture (the shelter spots included) first, then walls, then floors.
	step, ok := stepOf(spot, craft, wall)
	if !ok || step.Phase != GroundFurniture || len(step.Targets) != 2 {
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
	keeper.Rooms = append(append([]LayoutRoom(nil), next.Rooms...), LayoutRoom{Role: ModuleReserve, Interior: Rectangle{X: in.X - 4, Z: in.Z, Width: 3, Height: 3}, DoorRot: domain.North})
	heldWall := playerRow("held", "Wall", "ancient_wall_door", shared, shared, true)
	if work := PlannedGroundWork([]ClearanceTarget{heldWall}, nil, grounds, nil, RetiredGroundOf(keeper)); len(work) != 0 {
		t.Fatalf("a kept room's wall is demolition work: %v", work)
	}
}
