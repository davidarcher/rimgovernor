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
