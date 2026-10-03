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
	return RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "r1", Enclosed: domain.Known(true), Cells: cells}}}
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
	if step := NextTombStep(plan, RoomObservation{Shapes: testShapes}, dead, nil, true); step.Kind != TombShell || step.Room != room {
		t.Fatalf("unbuilt tomb: %+v", step)
	}
	step := NextTombStep(plan, tombStanding(room), dead, nil, true)
	if step.Kind != TombPlace || step.Piece.Def != SarcophagusDefinition {
		t.Fatalf("standing tomb: %+v", step)
	}
	first := step.Piece
	built := []CurrentBuilding{sarcophagus(t, "Sarcophagus_1", first)}
	if step := NextTombStep(plan, tombStanding(room), dead, built, true); step.Kind != TombNone || step.Empty != 0 {
		t.Fatalf("an empty sarcophagus waits: %+v", step)
	}
	// Filled, a second death places the next slot.
	two := append(dead, WasteItem{ID: "Corpse_0", Kind: "corpse", State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: "Sarcophagus_1"})
	step = NextTombStep(plan, tombStanding(room), two, built, true)
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
	if step := NextTombStep(plan, tombStanding(room), waste, nil, true); step.Kind != TombNone {
		t.Fatalf("no colonist died: %+v", step)
	}
}

func TestTombOwedFallsBackToAGrave(t *testing.T) {
	plan, _ := tombFixture()
	dead := domain.Known([]WasteItem{{ID: "Corpse_1", State: WasteExposed, CorpseOf: domain.CorpseColonist}})
	census := domain.Known(CurrentConstruction{Colony: true})
	if v, known := TombOwed(domain.Known(false), domain.Known(plan), domain.Known(RoomObservation{Shapes: testShapes}), dead, census).Value(); !known || !v {
		t.Errorf("unresearched sarcophagus still owes a grave: %v %v", v, known)
	}
	if v, known := TombOwed(domain.Known(true), domain.Known(plan), domain.Known(RoomObservation{Shapes: testShapes}), dead, census).Value(); !known || !v {
		t.Errorf("a dead colonist owes a tomb: %v %v", v, known)
	}
	if _, known := TombOwed(domain.Known(true), domain.Unknown[LayoutPlan](), domain.Known(RoomObservation{Shapes: testShapes}), dead, census).Value(); known {
		t.Error("an unknown plan leaves the tomb unknown")
	}
}

func TestCoreGrowPlansATomb(t *testing.T) {
	if coreRoomSize[ModuleTomb] != [2]int32{5, 5} || !containsRole(coreBaseRooms, ModuleTomb) {
		t.Fatal("the tomb is not a base room")
	}
	if role, ok := LayoutModule(RoomRoleTomb); !ok || role != ModuleTomb {
		t.Fatal("the Tomb role has no layout module")
	}
}

func TestTombStepGrowsAnotherTombWhenFull(t *testing.T) {
	plan, room := tombFixture()
	in, _ := InteriorRoomFromLayout(room, testShapes)
	interior, ok := PlanInterior(in, testShapes[SarcophagusDefinition])
	if !ok {
		t.Fatal("no template")
	}
	var built []CurrentBuilding
	var waste []WasteItem
	for _, p := range interior.Pieces {
		id := "Sarcophagus_" + p.Slot
		built = append(built, sarcophagus(t, id, p))
		waste = append(waste, WasteItem{ID: "Buried_" + p.Slot, State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: id})
	}
	waste = append(waste, WasteItem{ID: "Corpse_new", State: WasteExposed, CorpseOf: domain.CorpseColonist})
	if step := NextTombStep(plan, tombStanding(room), waste, built, true); step.Kind != TombFull {
		t.Fatalf("every slot filled: %+v", step)
	}
	second := LayoutRoom{Role: ModuleTomb, Interior: Rectangle{X: 16, Z: 20, Width: 5, Height: 5}, Door: domain.Cell{X: 18, Z: 19}, DoorRot: domain.North}
	plan.Rooms = append(plan.Rooms, second)
	if step := NextTombStep(plan, tombStanding(room), waste, built, true); step.Kind != TombShell || step.Room != second {
		t.Fatalf("second tomb: %+v", step)
	}
	if plan.TombRooms() != 2 {
		t.Fatal(plan.TombRooms())
	}
}

func TestTombStepGravesWithoutASarcophagus(t *testing.T) {
	plan, room := tombFixture()
	dead := []WasteItem{{ID: "Corpse_1", State: WasteExposed, CorpseOf: domain.CorpseColonist}}
	step := NextTombStep(plan, tombStanding(room), dead, nil, false)
	if step.Kind != TombGrave || step.Graves != 0 {
		t.Fatalf("no sarcophagus: %+v", step)
	}
	g, err := domain.NewBuilding(GraveDefinition, domain.Cell{X: 40, Z: 40}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	grave := CurrentBuilding{ID: "Grave_1", Building: g, Cells: []domain.Cell{{X: 40, Z: 40}, {X: 40, Z: 41}}}
	if step := NextTombStep(plan, tombStanding(room), dead, []CurrentBuilding{grave}, false); step.Kind != TombNone {
		t.Fatalf("an empty grave waits: %+v", step)
	}
}
