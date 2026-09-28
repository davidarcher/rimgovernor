package policy

import (
	"math"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWallOffMountainPocket(t *testing.T) {
	plan := LayoutPlan{
		Rooms:        []LayoutRoom{{Role: ModuleBedroom, Interior: Rectangle{X: 40, Z: 40, Width: 5, Height: 5}, DoorRot: domain.North}},
		Reservations: []LayoutReservation{{Kind: ReservePerimeter, Area: Rectangle{X: 30, Z: 30, Width: 30, Height: 3}}, {Kind: ReservePocketWall, Area: Rectangle{X: 1, Z: 1, Width: 1, Height: 1}}},
	}
	var cells []SurveyCell
	open := func(r Rectangle) {
		for _, c := range rectCells(r) {
			cells = append(cells, SurveyCell{Cell: c, Walkable: true, ThickRoof: true})
		}
	}
	open(Rectangle{X: 34, Z: 36, Width: 3, Height: 2}) // small pocket inside the ring: walled
	open(Rectangle{X: 50, Z: 50, Width: 5, Height: 5}) // 25 cells: too big
	open(Rectangle{X: 43, Z: 43, Width: 2, Height: 1}) // under the bedroom: used
	open(Rectangle{X: 90, Z: 90, Width: 2, Height: 2}) // far from the base
	cells = append(cells, SurveyCell{Cell: domain.Cell{X: 38, Z: 36}, Rock: true, ThickRoof: true})
	cells = append(cells, SurveyCell{Cell: domain.Cell{X: 38, Z: 38}, Walkable: true}) // open sky
	s := MapSurvey{Bounds: Bounds{Width: 100, Height: 100}, Cells: cells}

	got := PlanMountainPockets(plan, s)
	var walled []domain.Cell
	for _, r := range got.Reservations {
		if r.Kind == ReservePocketWall {
			walled = append(walled, rectCells(r.Area)...)
		}
	}
	slices.SortFunc(walled, func(a, b domain.Cell) int {
		if cellLess(a, b) {
			return -1
		}
		return 1
	})
	want := rectCells(Rectangle{X: 34, Z: 36, Width: 3, Height: 2})
	if !slices.Equal(walled, want) {
		t.Fatalf("walled %v, want %v", walled, want)
	}
	sections, err := PocketSections(got, "Wall")
	if err != nil || len(sections) != 2 || len(sections[0].Buildings) != 3 || sections[0].Buildings[0].Definition() != "Wall" || !IsPerimeterTier(sections[0].Name) {
		t.Fatalf("sections %+v err %v", sections, err)
	}
	if !samePerimeter(got, got) || samePerimeter(plan, got) {
		t.Fatal("pocket walls are not part of the perimeter's identity")
	}
	if none := PlanMountainPockets(LayoutPlan{Rooms: plan.Rooms}, s); len(none.Reservations) != 0 {
		t.Fatalf("a plan without a perimeter walls %v", none.Reservations)
	}
}

func TestLightBaseRooms(t *testing.T) {
	plan := LayoutPlan{Rooms: []LayoutRoom{
		{Role: ModuleBedroom, Interior: Rectangle{X: 10, Z: 10, Width: 4, Height: 4}},
		{Role: ModuleWorkshop, Interior: Rectangle{X: 20, Z: 10, Width: 15, Height: 7}},
		{Role: ModuleReserve, Interior: Rectangle{X: 40, Z: 10, Width: 6, Height: 6}},
	}}
	lamps := BaseRoomLamps(plan)
	if len(lamps) != 3 {
		t.Fatalf("lamps %v, want one in the bedroom and two in the workshop", lamps)
	}
	for _, r := range plan.Rooms {
		for _, c := range rectCells(r.Interior) {
			best := math.Inf(1)
			for _, l := range lamps {
				best = min(best, distance(l, c))
			}
			if r.Role == ModuleReserve {
				if best <= lampReach {
					t.Fatalf("reserve cell %v lit", c)
				}
				continue
			}
			if best > lampReach {
				t.Fatalf("%s cell %v is %.1f from a lamp", r.Role, c, best)
			}
		}
	}
	sections, err := LightSections(plan, "StandingLamp")
	if err != nil || len(sections) != 1 || len(sections[0].Buildings) != 3 || !IsPerimeterTier(sections[0].Name) {
		t.Fatalf("sections %+v err %v", sections, err)
	}
}
