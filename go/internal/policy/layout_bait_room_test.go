package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestBaitRoomPlacement(t *testing.T) {
	plan := LayoutPlan{
		Reservations: []LayoutReservation{{Kind: ReservePerimeter, Area: Rectangle{X: 10, Z: 10, Width: 20, Height: 3}}, {Kind: ReserveBaitRoom, Area: Rectangle{X: 1, Z: 1, Width: 5, Height: 5}}},
	}
	var cells []SurveyCell
	set := func(r Rectangle, c SurveyCell) {
		for _, p := range rectCells(r) {
			c.Cell = p
			cells = append(cells, c)
		}
	}
	// Mountain across x 40..79, z 0..39: rock with two open caves.
	set(Rectangle{X: 40, Z: 0, Width: 40, Height: 40}, SurveyCell{Rock: true, ThickRoof: true})
	set(Rectangle{X: 41, Z: 11, Width: 3, Height: 3}, SurveyCell{Walkable: true, ThickRoof: true}) // inside the gap: too close
	set(Rectangle{X: 70, Z: 11, Width: 3, Height: 3}, SurveyCell{Walkable: true, ThickRoof: true}) // far enough
	set(Rectangle{X: 71, Z: 10, Width: 1, Height: 1}, SurveyCell{Walkable: true, ThickRoof: true}) // its door
	set(Rectangle{X: 60, Z: 30, Width: 3, Height: 3}, SurveyCell{Walkable: true, ThickRoof: true}) // no door cell
	var survey []SurveyCell
	seen := map[domain.Cell]int{}
	for _, c := range cells {
		if i, ok := seen[c.Cell]; ok {
			survey[i] = c
			continue
		}
		seen[c.Cell] = len(survey)
		survey = append(survey, c)
	}
	s := MapSurvey{Bounds: Bounds{Width: 100, Height: 100}, Cells: survey}

	got := PlanBaitRoom(plan, s)
	var rooms []Rectangle
	walls := 0
	for _, r := range got.Reservations {
		switch r.Kind {
		case ReserveBaitRoom:
			rooms = append(rooms, r.Area)
		case ReserveBaitWall:
			walls++
		}
	}
	want := Rectangle{X: 69, Z: 10, Width: 5, Height: 5}
	if len(rooms) != 1 || rooms[0] != want || walls != 0 {
		t.Fatalf("bait rooms %v walls %d, want %v in solid rock", rooms, walls, want)
	}
	if !samePerimeter(got, got) || samePerimeter(plan, got) {
		t.Fatal("the bait room is not part of the perimeter's identity")
	}
	if len(BaseRoomLamps(got)) != 0 {
		t.Fatal("the bait room is lit")
	}

	sections, err := BaitRoomSections(got, "Wall", "Door", "Stool", false)
	if err != nil || len(sections) != 1 || !IsPerimeterTier(sections[0].Name) {
		t.Fatalf("sections %+v err %v", sections, err)
	}
	count := map[string]int{}
	for _, b := range sections[0].Buildings {
		count[b.Definition()]++
		if b.Definition() == BaitIED && b.Cell() != (domain.Cell{X: 71, Z: 12}) {
			t.Fatalf("IED at %v, want the floor's centre", b.Cell())
		}
	}
	if count["Door"] != 1 || count[BaitIED] != 1 || count["Stool"] != 7 || count["Wall"] != 0 {
		t.Fatalf("bait room holds %v", count)
	}
	sections, _ = BaitRoomSections(got, "Wall", "Door", "Stool", true)
	count = map[string]int{}
	for _, b := range sections[0].Buildings {
		count[b.Definition()]++
	}
	if count[BaitTrap] != 8 || count[BaitIED] != 0 || count["Stool"] != 0 {
		t.Fatalf("jelly bait room holds %v", count)
	}

	// An open wall cell takes a built wall.
	for i, c := range survey {
		if c.Cell == (domain.Cell{X: 73, Z: 12}) {
			survey[i].Rock, survey[i].Walkable = false, true
		}
	}
	walled := PlanBaitRoom(plan, s)
	sections, _ = BaitRoomSections(walled, "Wall", "Door", "Stool", false)
	if len(sections) != 1 || sections[0].Buildings[0].Definition() != "Wall" || sections[0].Buildings[0].Cell() != (domain.Cell{X: 73, Z: 12}) {
		t.Fatalf("open wall cell not walled: %+v", sections)
	}

	if none := PlanBaitRoom(LayoutPlan{}, s); len(none.Reservations) != 0 {
		t.Fatalf("a plan without a perimeter baits %v", none.Reservations)
	}
}
