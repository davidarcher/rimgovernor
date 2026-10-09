package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tombFixture() (LayoutPlan, PlannedRoom) {
	room := PlannedRoom{Role: PlannedTomb, Interior: Rectangle{X: 10, Z: 20, Width: 5, Height: 5}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	return LayoutPlan{Rooms: []PlannedRoom{room}}, room
}

func tombStanding(room PlannedRoom) RoomObservation {
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
	b, err := domain.NewBuilding(testSarcophagus, p.Anchor(), p.Rot, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: id, Building: b, Cells: rectCells(p.Rect)}
}

// wantedInterior is the interior piece a template piece was planned from.
func wantedInterior(w WantedPiece) InteriorPiece {
	return InteriorPiece{Slot: w.Slot, Def: w.DefName, Size: w.Size, Rot: w.Rot, Rect: Rectangle{X: w.Minimum.X, Z: w.Minimum.Z, Width: w.Maximum.X - w.Minimum.X + 1, Height: w.Maximum.Z - w.Minimum.Z + 1}}
}

func TestTombStepReconcilesTheRoomForADeadColonist(t *testing.T) {
	plan, room := tombFixture()
	dead := []WasteItem{{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseColonist}}
	step := NextTombStep(plan, dead, nil, testShapes, true, StrangerTomb{})
	if step.Kind != TombReconcile || !step.Room.Same(room) || len(step.Template) != 1 || step.Template[0].DefName != testSarcophagus {
		t.Fatalf("tomb: %+v", step)
	}
	// The reconciler raises the ring first; the sarcophagus follows once it
	// stands, installed from stock when one is packed.
	in := ReconcileInput{Plan: plan, Room: room, Ground: GroundCensus{}, Rooms: tombStanding(room), Furniture: step.Template}
	if ops := ReconcileRoom(in); len(ops) == 0 || ops[0].Kind != OpWallIn {
		t.Fatalf("unbuilt ring: %+v", ops)
	}
	in.Ground = ringWalls(plan, room)
	if ops := ReconcileRoom(in); len(ops) != 1 || ops[0].Kind != OpBuild {
		t.Fatalf("build on site: %+v", ops)
	}
	in.Stock = map[string]int{testSarcophagus: 1}
	if ops := ReconcileRoom(in); len(ops) != 1 || ops[0].Kind != OpInstall {
		t.Fatalf("stock first: %+v", ops)
	}
	first := wantedInterior(step.Template[0])
	built := []CurrentBuilding{sarcophagus(t, "Sarcophagus_1", first)}
	if step := NextTombStep(plan, dead, built, testShapes, true, StrangerTomb{}); step.Kind != TombNone || step.Empty != 0 {
		t.Fatalf("an empty sarcophagus waits: %+v", step)
	}
	// Filled, a second death places the next slot.
	two := append(dead, WasteItem{ID: "Corpse_0", Kind: "corpse", State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: "Sarcophagus_1"})
	step = NextTombStep(plan, two, built, testShapes, true, StrangerTomb{})
	if step.Kind != TombReconcile || len(step.Template) != 1 || step.Template[0].Slot == first.Slot {
		t.Fatalf("second death: %+v", step)
	}
}

func TestTombStepIgnoresStrangersAndAnimals(t *testing.T) {
	plan, _ := tombFixture()
	waste := []WasteItem{
		{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger},
		{ID: "Corpse_2", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseAnimal},
	}
	if step := NextTombStep(plan, waste, nil, testShapes, true, StrangerTomb{}); step.Kind != TombNone {
		t.Fatalf("no colonist died: %+v", step)
	}
}

func TestTombOwedFallsBackToAGrave(t *testing.T) {
	plan, _ := graveyardPlan(t)
	dead := domain.Known([]WasteItem{{ID: "Corpse_1", State: WasteExposed, CorpseOf: domain.CorpseColonist}})
	census := domain.Known(CurrentConstruction{Colony: true})
	if v, known := TombOwed(testShapes, domain.Known(false), domain.Known(plan), domain.Known(RoomObservation{Shapes: testShapes}), dead, census, StrangerTomb{}).Value(); !known || !v {
		t.Errorf("unresearched sarcophagus still owes a grave: %v %v", v, known)
	}
	if v, known := TombOwed(testShapes, domain.Known(true), domain.Known(plan), domain.Known(RoomObservation{Shapes: testShapes}), dead, census, StrangerTomb{}).Value(); !known || !v {
		t.Errorf("a dead colonist owes a tomb: %v %v", v, known)
	}
	if _, known := TombOwed(testShapes, domain.Known(true), domain.Unknown[LayoutPlan](), domain.Known(RoomObservation{Shapes: testShapes}), dead, census, StrangerTomb{}).Value(); known {
		t.Error("an unknown plan leaves the tomb unknown")
	}
}

func TestCoreGrowPlansATomb(t *testing.T) {
	if coreRoomSize[PlannedTomb] != [2]int32{5, 5} || containsRole(coreBaseRooms, PlannedTomb) {
		t.Fatal("the tomb is grown on demand, not a base room")
	}
	if p := corePlan(coreTestZones(), 3, TechTierCamp); p.TombRooms() != 0 {
		t.Fatal("a fresh plan holds no tomb", p.TombRooms())
	}
	if p := growPlan(corePlan(coreTestZones(), 3, TechTierCamp), 3, 1, TechTierCamp); p.TombRooms() != 1 {
		t.Fatal("a dead colonist grows one tomb", p.TombRooms())
	}
	if role, ok := PlannedRoleFor(RoomRoleTomb); !ok || role != PlannedTomb {
		t.Fatal("the Tomb role has no layout module")
	}
}

func TestTombStepGrowsAnotherTombWhenFull(t *testing.T) {
	plan, room := tombFixture()
	in, _ := InteriorRoomFromLayout(room, testShapes)
	interior, ok := PlanInterior(in, testShapes.Defs[testSarcophagus])
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
	if step := NextTombStep(plan, waste, built, testShapes, true, StrangerTomb{}); step.Kind != TombFull {
		t.Fatalf("every slot filled: %+v", step)
	}
	second := PlannedRoom{Role: PlannedTomb, Interior: Rectangle{X: 16, Z: 20, Width: 5, Height: 5}, Door: domain.Cell{X: 18, Z: 19}, DoorRot: domain.North}
	plan.Rooms = append(plan.Rooms, second)
	if step := NextTombStep(plan, waste, built, testShapes, true, StrangerTomb{}); step.Kind != TombReconcile || !step.Room.Same(second) {
		t.Fatalf("second tomb: %+v", step)
	}
	if plan.TombRooms() != 2 {
		t.Fatal(plan.TombRooms())
	}
}

func TestTombStepGravesInTheGraveyardWithoutASarcophagus(t *testing.T) {
	plan, yard := graveyardPlan(t)
	dead := []WasteItem{{ID: "Corpse_1", State: WasteExposed, CorpseOf: domain.CorpseColonist}}
	step := NextTombStep(plan, dead, nil, testShapes, false, StrangerTomb{})
	slots := GraveyardSlots(yard.Interior)
	if step.Kind != TombReconcile || !step.Room.Same(yard) || len(step.Template) != 1 || step.Template[0].DefName != GraveDefinition || step.Template[0].Minimum != (domain.Cell{X: slots[0].X, Z: slots[0].Z}) {
		t.Fatalf("no sarcophagus: %+v", step)
	}
	g, err := domain.NewBuilding(GraveDefinition, domain.Cell{X: 40, Z: 40}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	grave := CurrentBuilding{ID: "Grave_1", Building: g, Cells: []domain.Cell{{X: 40, Z: 40}, {X: 40, Z: 41}}}
	if step := NextTombStep(plan, dead, []CurrentBuilding{grave}, testShapes, false, StrangerTomb{}); step.Kind != TombNone {
		t.Fatalf("an empty grave waits: %+v", step)
	}
	// The next grave takes the next slot, not one a grave stands on.
	g, err = domain.NewBuilding(GraveDefinition, domain.Cell{X: slots[0].X, Z: slots[0].Z}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	filled := CurrentBuilding{ID: "Grave_2", Building: g, Cells: rectCells(slots[0])}
	two := append(dead, WasteItem{ID: "Corpse_0", State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: "Grave_2"})
	step = NextTombStep(plan, two, []CurrentBuilding{filled}, testShapes, false, StrangerTomb{})
	if step.Kind != TombReconcile || step.Template[0].Minimum != (domain.Cell{X: slots[1].X, Z: slots[1].Z}) {
		t.Fatalf("second grave: %+v", step)
	}
}

// No grave is placed outside a graveyard: with no graveyard planned, or none of
// its slots free, the body waits (#2196).
func TestNoGraveOutsideTheGraveyard(t *testing.T) {
	dead := []WasteItem{{ID: "Corpse_1", State: WasteExposed, CorpseOf: domain.CorpseColonist}}
	tombPlan, _ := tombFixture()
	if step := NextTombStep(tombPlan, dead, nil, testShapes, false, StrangerTomb{}); step.Kind != TombNone {
		t.Fatalf("no graveyard planned: %+v", step)
	}
	plan, yard := graveyardPlan(t)
	var built []CurrentBuilding
	for i, slot := range GraveyardSlots(yard.Interior) {
		g, err := domain.NewBuilding(GraveDefinition, domain.Cell{X: slot.X, Z: slot.Z}, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		built = append(built, CurrentBuilding{ID: "Grave_" + string(rune('a'+i)), Building: g, Cells: rectCells(slot)})
	}
	waste := []WasteItem{{ID: "Corpse_1", State: WasteExposed, CorpseOf: domain.CorpseColonist}, {ID: "Corpse_2", State: WasteExposed, CorpseOf: domain.CorpseColonist}}
	for i := range built {
		waste = append(waste, WasteItem{ID: "Buried_" + built[i].ID, State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: built[i].ID})
	}
	if step := NextTombStep(plan, waste, built, testShapes, false, StrangerTomb{}); step.Kind != TombNone {
		t.Fatalf("a full graveyard: %+v", step)
	}
}

// A further graveyard is asked for when the empty graves run out, or the
// graveyard is 0.85 used, and only while no sarcophagus can be had (#2196).
func TestFurtherGraveyardWhenEmptyGravesRunOut(t *testing.T) {
	plan, yard := graveyardPlan(t)
	slots := GraveyardSlots(yard.Interior)
	graves := func(n int) ([]CurrentBuilding, []WasteItem) {
		var built []CurrentBuilding
		var waste []WasteItem
		for i := 0; i < n; i++ {
			g, err := domain.NewBuilding(GraveDefinition, domain.Cell{X: slots[i].X, Z: slots[i].Z}, domain.North, "")
			if err != nil {
				t.Fatal(err)
			}
			id := "Grave_" + string(rune('a'+i))
			built = append(built, CurrentBuilding{ID: id, Building: g, Cells: rectCells(slots[i])})
			waste = append(waste, WasteItem{ID: "Buried_" + id, State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: id})
		}
		return built, waste
	}
	dead := WasteItem{ID: "Corpse_new", State: WasteExposed, CorpseOf: domain.CorpseColonist}
	built, waste := graves(5)
	if got := GraveyardsWanted(plan, append(waste, dead), built, testShapes, false); got != 0 {
		t.Fatalf("slots to spare: %d", got)
	}
	built, waste = graves(GraveyardGraves)
	if got := GraveyardsWanted(plan, append(waste, dead), built, testShapes, false); got != 2 {
		t.Fatalf("every slot filled: %d", got)
	}
	if got := GraveyardsWanted(plan, append(waste, dead), built, testShapes, true); got != 0 {
		t.Fatalf("a sarcophagus can be had: %d", got)
	}
	built, waste = graves(11)
	if got := GraveyardsWanted(plan, waste, built, testShapes, false); got != 2 {
		t.Fatalf("0.85 used with nobody dead: %d", got)
	}
	built, waste = graves(10)
	if got := GraveyardsWanted(plan, waste, built, testShapes, false); got != 0 {
		t.Fatalf("under 0.85 used: %d", got)
	}
	if got := GraveyardsWanted(LayoutPlan{}, nil, nil, testShapes, false); got != 0 {
		t.Fatalf("no graveyard planned: %d", got)
	}
}
