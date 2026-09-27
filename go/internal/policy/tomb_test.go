package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tombFixture() (LayoutPlan, LayoutRoom) {
	room := LayoutRoom{Role: ModuleTomb, Interior: Rectangle{X: 10, Z: 20, Width: 5, Height: 5}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	return LayoutPlan{Rooms: []LayoutRoom{room}}, room
}

func tombStanding(room LayoutRoom) RoomObservation {
	var cells []domain.Cell
	for z := room.Interior.Z; z < room.Interior.Z+room.Interior.Height; z++ {
		for x := room.Interior.X; x < room.Interior.X+room.Interior.Width; x++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return RoomObservation{Rooms: []Room{{ID: "r1", Enclosed: domain.Known(true), Cells: cells}}}
}

func sarcophagus(t *testing.T, id string, p InteriorPiece) CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding(SarcophagusDefinition, p.Anchor(), p.Rot, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: id, Building: b, Cells: rectCells(p.Rect)}
}

func TestTombStepShellsThenPlacesForADeadColonist(t *testing.T) {
	plan, room := tombFixture()
	dead := []WasteItem{{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseColonist}}
	if step := NextTombStep(plan, RoomObservation{}, dead, nil); step.Kind != TombShell || step.Room != room {
		t.Fatalf("unbuilt tomb: %+v", step)
	}
	step := NextTombStep(plan, tombStanding(room), dead, nil)
	if step.Kind != TombPlace || step.Piece.Def != SarcophagusDefinition {
		t.Fatalf("standing tomb: %+v", step)
	}
	first := step.Piece
	built := []CurrentBuilding{sarcophagus(t, "Sarcophagus_1", first)}
	if step := NextTombStep(plan, tombStanding(room), dead, built); step.Kind != TombNone || step.Empty != 0 {
		t.Fatalf("an empty sarcophagus waits: %+v", step)
	}
	// Filled, a second death places the next slot.
	two := append(dead, WasteItem{ID: "Corpse_0", Kind: "corpse", State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: "Sarcophagus_1"})
	step = NextTombStep(plan, tombStanding(room), two, built)
	if step.Kind != TombPlace || step.Piece.Slot == first.Slot {
		t.Fatalf("second death: %+v", step)
	}
}

func TestTombStepIgnoresStrangersAndAnimals(t *testing.T) {
	plan, room := tombFixture()
	waste := []WasteItem{
		{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger},
		{ID: "Corpse_2", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseAnimal},
	}
	if step := NextTombStep(plan, tombStanding(room), waste, nil); step.Kind != TombNone {
		t.Fatalf("no colonist died: %+v", step)
	}
}

func TestTombOwedWaitsOnTheSarcophagus(t *testing.T) {
	plan, _ := tombFixture()
	dead := domain.Known([]WasteItem{{ID: "Corpse_1", State: WasteExposed, CorpseOf: domain.CorpseColonist}})
	census := domain.Known(CurrentConstruction{Colony: true})
	if v, known := TombOwed(domain.Known(false), domain.Known(plan), domain.Known(RoomObservation{}), dead, census).Value(); !known || v {
		t.Errorf("unresearched sarcophagus owes no tomb: %v %v", v, known)
	}
	if v, known := TombOwed(domain.Known(true), domain.Known(plan), domain.Known(RoomObservation{}), dead, census).Value(); !known || !v {
		t.Errorf("a dead colonist owes a tomb: %v %v", v, known)
	}
	if _, known := TombOwed(domain.Known(true), domain.Unknown[LayoutPlan](), domain.Known(RoomObservation{}), dead, census).Value(); known {
		t.Error("an unknown plan leaves the tomb unknown")
	}
}

func TestCoreGrowPlansATomb(t *testing.T) {
	if coreRoomSize[ModuleTomb] != [2]int32{5, 5} || coreBaseRooms[len(coreBaseRooms)-1] != ModuleTomb {
		t.Fatal("the tomb is not a base room")
	}
	if role, ok := LayoutModule(RoomRoleTomb); !ok || role != ModuleTomb {
		t.Fatal("the Tomb role has no layout module")
	}
}
