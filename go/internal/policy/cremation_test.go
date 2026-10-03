package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCremationPlacesInAWorkshopThenBills(t *testing.T) {
	room := LayoutRoom{Role: ModuleWorkshop, Interior: Rectangle{X: 10, Z: 20, Width: 7, Height: 5}, Door: domain.Cell{X: 13, Z: 19}, DoorRot: domain.North}
	plan := LayoutPlan{Rooms: []LayoutRoom{room}}
	raider := []WasteItem{{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger}}
	if step := NextCremationStep(plan, RoomObservation{Shapes: testShapes}, raider, nil, false); step.Kind != CremationNone {
		t.Fatalf("no standing workshop: %+v", step)
	}
	step := NextCremationStep(plan, tombStanding(room), raider, nil, false)
	if step.Kind != CremationPlace || step.Piece.Def != CrematoriumDefinition || step.Piece.Size != (domain.Cell{X: 3, Z: 2}) {
		t.Fatalf("standing workshop: %+v", step)
	}
	b, err := domain.NewBuilding(CrematoriumDefinition, step.Piece.Anchor(), step.Piece.Rot, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	built := []CurrentBuilding{{ID: "ElectricCrematorium_1", Building: b, Cells: rectCells(step.Piece.Rect)}}
	if step := NextCremationStep(plan, tombStanding(room), raider, built, false); step.Kind != CremationBill || step.Bench != "ElectricCrematorium_1" {
		t.Fatalf("built crematorium: %+v", step)
	}
}

func TestCremationTakesSpoiledAnimalsButNotFreshOnes(t *testing.T) {
	room := LayoutRoom{Role: ModuleWorkshop, Interior: Rectangle{X: 10, Z: 20, Width: 7, Height: 5}, Door: domain.Cell{X: 13, Z: 19}, DoorRot: domain.North}
	plan := LayoutPlan{Rooms: []LayoutRoom{room}}
	rooms := tombStanding(room)
	cases := []struct {
		name   string
		item   WasteItem
		owed   int
		places bool
	}{
		{"rotting", WasteItem{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseAnimal, RotStage: domain.RotRotting}, 1, true},
		{"desiccated", WasteItem{ID: "Corpse_2", Kind: "corpse", State: WasteRelocated, CorpseOf: domain.CorpseAnimal, RotStage: domain.RotDessicated}, 1, true},
		{"fresh", WasteItem{ID: "Corpse_3", State: WasteExposed, CorpseOf: domain.CorpseAnimal, RotStage: domain.RotFresh}, 0, false},
		{"stage unknown", WasteItem{ID: "Corpse_4", State: WasteExposed, CorpseOf: domain.CorpseAnimal}, 0, false},
		{"buried", WasteItem{ID: "Corpse_5", Kind: "corpse", State: WasteBuried, CorpseOf: domain.CorpseAnimal, RotStage: domain.RotRotting}, 0, false},
		{"colonist", WasteItem{ID: "Corpse_6", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseColonist, RotStage: domain.RotRotting}, 0, false},
	}
	for _, tc := range cases {
		step := NextCremationStep(plan, rooms, []WasteItem{tc.item}, nil, false)
		if step.Animals != tc.owed || (step.Kind == CremationPlace) != tc.places {
			t.Errorf("%s: %+v", tc.name, step)
		}
	}
}

func TestCremationNeverTakesColonistsOrAnimals(t *testing.T) {
	room := LayoutRoom{Role: ModuleWorkshop, Interior: Rectangle{X: 10, Z: 20, Width: 7, Height: 5}, Door: domain.Cell{X: 13, Z: 19}, DoorRot: domain.North}
	plan := LayoutPlan{Rooms: []LayoutRoom{room}}
	waste := []WasteItem{
		{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseColonist},
		{ID: "Corpse_2", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseAnimal},
		{ID: "Corpse_3", Kind: "corpse", State: WasteBuried, CorpseOf: domain.CorpseStranger},
	}
	if step := NextCremationStep(plan, tombStanding(room), waste, nil, false); step.Kind != CremationNone {
		t.Fatalf("no unburied stranger: %+v", step)
	}
	census := domain.Known(CurrentConstruction{Colony: true})
	if v, known := CremationOwed(domain.Known(false), domain.Known(plan), domain.Known(RoomObservation{Shapes: testShapes}), domain.Known(waste), census, false).Value(); !known || v {
		t.Fatalf("unavailable crematorium owed: %v %v", v, known)
	}
}
