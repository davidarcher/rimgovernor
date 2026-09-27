package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCremationPlacesInAWorkshopThenBills(t *testing.T) {
	room := LayoutRoom{Role: ModuleWorkshop, Interior: Rectangle{X: 10, Z: 20, Width: 7, Height: 5}, Door: domain.Cell{X: 13, Z: 19}, DoorRot: domain.North}
	plan := LayoutPlan{Rooms: []LayoutRoom{room}}
	raider := []WasteItem{{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger}}
	if step := NextCremationStep(plan, RoomObservation{}, raider, nil); step.Kind != CremationNone {
		t.Fatalf("no standing workshop: %+v", step)
	}
	step := NextCremationStep(plan, tombStanding(room), raider, nil)
	if step.Kind != CremationPlace || step.Piece.Def != CrematoriumDefinition || step.Piece.Size != (domain.Cell{X: 3, Z: 2}) {
		t.Fatalf("standing workshop: %+v", step)
	}
	b, err := domain.NewBuilding(CrematoriumDefinition, step.Piece.Anchor(), step.Piece.Rot, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	built := []CurrentBuilding{{ID: "ElectricCrematorium_1", Building: b, Cells: rectCells(step.Piece.Rect)}}
	if step := NextCremationStep(plan, tombStanding(room), raider, built); step.Kind != CremationBill || step.Bench != "ElectricCrematorium_1" {
		t.Fatalf("built crematorium: %+v", step)
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
	if step := NextCremationStep(plan, tombStanding(room), waste, nil); step.Kind != CremationNone {
		t.Fatalf("no unburied stranger: %+v", step)
	}
	census := domain.Known(CurrentConstruction{Colony: true})
	if v, known := CremationOwed(domain.Known(false), domain.Known(plan), domain.Known(RoomObservation{}), domain.Known(waste), census).Value(); !known || v {
		t.Fatalf("unavailable crematorium owed: %v %v", v, known)
	}
}
