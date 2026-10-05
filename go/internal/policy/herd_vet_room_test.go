package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestVetRoomReadyNeedsAShelledRoomAndAMedicalBed(t *testing.T) {
	plan := herdTestPlan(t, 20)
	vet := plan.HerdRooms(PlannedVetRoom)[0]
	layout, ok := PlanInterior(mustInterior(t, vet), testHerdFurniture.Bed)
	if !ok {
		t.Fatal("no vet layout")
	}
	bed := bedAt(t, testAnimalBed, layout.Pieces[0])
	census := func(medical domain.Fact[bool]) []SleepingBed {
		return []SleepingBed{{ID: bed.ID, Medical: medical}}
	}
	built := []CurrentBuilding{bed}
	cases := []struct {
		name  string
		rooms RoomObservation
		built []CurrentBuilding
		beds  []SleepingBed
		want  domain.Fact[bool]
	}{
		{"flagged bed in a standing room", standing(vet), built, census(domain.Known(true)), domain.Known(true)},
		{"unflagged bed", standing(vet), built, census(domain.Known(false)), domain.Known(false)},
		{"unread flag stays unknown", standing(vet), built, census(domain.Unknown[bool]()), domain.Unknown[bool]()},
		{"bed without a census row stays unknown", standing(vet), built, nil, domain.Unknown[bool]()},
		{"room not shelled", RoomObservation{Shapes: testShapes}, built, census(domain.Known(true)), domain.Known(false)},
		{"no bed built", standing(vet), nil, nil, domain.Known(false)},
	}
	for _, c := range cases {
		got := VetRoomReady(plan, c.rooms, c.built, c.beds, testAnimalBed)
		gv, gk := got.Value()
		wv, wk := c.want.Value()
		if gk != wk || gv != wv {
			t.Errorf("%s: got %v known %v, want %v known %v", c.name, gv, gk, wv, wk)
		}
	}
}

func TestVetRoomAreaCoversTheInteriorAndNotTheAnimalAreas(t *testing.T) {
	plan := herdTestPlan(t, 20)
	vet := plan.HerdRooms(PlannedVetRoom)[0]
	cells := plan.VetRoomCells()
	if len(cells) != len(rectCells(vet.Interior)) {
		t.Fatal("the area is the vet room interior", len(cells))
	}
	for _, a := range plan.AnimalAreas() {
		in := map[domain.Cell]bool{}
		for _, c := range rectCells(a) {
			in[c] = true
		}
		for _, c := range cells {
			if in[c] {
				t.Fatal("an animal area covers the vet room", c)
			}
		}
	}
}
