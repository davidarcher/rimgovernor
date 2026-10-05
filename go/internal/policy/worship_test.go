package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Recorded ideology facts: a building precept that took one definition and a
// ritual pattern whose obligation target filter accepts another. The names
// are fixture data; the policy reads whatever the game's defs name.
func worshipIdeoligion() Ideoligion {
	return Ideoligion{
		Defs: IdeologyDefs{Rituals: map[string]RitualDef{"Pattern": {Name: "Pattern", RequiredBuildings: []string{"TestIdeogram"}}}},
		Facts: IdeoligionFacts{
			Buildings: []HeldBuilding{{ID: "1", Def: "Precept_Altar", Building: "TestAltar"}},
			Rituals:   []HeldRitual{{ID: "2", Def: "Precept_Ritual", Pattern: "Pattern"}},
		},
	}
}

func TestWorshipRoomNeedIsOneOfEachRequiredBuilding(t *testing.T) {
	need, ok := WorshipRoomNeed(worshipIdeoligion())
	if !ok || need.Role != RoomRoleWorshipRoom || need.Module != PlannedWorship || len(need.Furniture) != 2 {
		t.Fatalf("need: %+v", need)
	}
	if f := need.Furniture; f[0].Defs[0] != "TestAltar" || f[1].Defs[0] != "TestIdeogram" || f[0].Count != 1 || f[1].Count != 1 {
		t.Fatalf("furniture: %+v", f)
	}
	if _, ok := WorshipRoomNeed(Ideoligion{}); ok {
		t.Fatal("an ideoligion without required buildings owes no room")
	}
}

func TestWorshipRoomIsGrownShelledAndFurnishedLikeAChildRoom(t *testing.T) {
	need, _ := WorshipRoomNeed(worshipIdeoligion())
	defs := furnitureDefs(map[string]Bounds{"TestAltar": {Width: 1, Height: 2}, "TestIdeogram": {Width: 1, Height: 1}})
	shape, ok := need.shape(defs)
	if !ok || shape.Module != PlannedWorship {
		t.Fatalf("shape: %+v", shape)
	}
	// Footprints come from the catalog; an unknown one owes nothing.
	if owed := ChildRoomsOwed(LayoutPlan{}, []ChildRoomNeed{need}, defs[:1]); len(owed) != 0 {
		t.Fatalf("a required building the catalog lacks stays unknown: %+v", owed)
	}
	if owed := ChildRoomsOwed(LayoutPlan{}, []ChildRoomNeed{need}, defs); len(owed) != 1 {
		t.Fatalf("owed: %+v", owed)
	}
	sizes := ChildRoomSizes(shape)
	if len(sizes) == 0 {
		t.Fatal("no room holds the buildings")
	}
	room := PlannedRoom{Role: PlannedWorship, Interior: Rectangle{X: 10, Z: 20, Width: sizes[0][0], Height: sizes[0][1]}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	plan := LayoutPlan{Rooms: []PlannedRoom{room}}
	if step := NextChildRoomStep(plan, RoomObservation{Shapes: testShapes}, GroundCensus{}, nil, []ChildRoomNeed{need}, defs); step.Kind != ChildRoomReconcile || !step.Room.Same(room) {
		t.Fatalf("unbuilt room: %+v", step)
	}
	step := NextChildRoomStep(plan, tombStanding(room), ringWalls(plan, room), nil, []ChildRoomNeed{need}, defs)
	if step.Kind != ChildRoomReconcile || len(step.Template) == 0 || step.Template[0].DefName != "TestAltar" {
		t.Fatalf("standing room: %+v", step)
	}
	if owed := ChildRoomsOwed(plan, []ChildRoomNeed{need}, defs); len(owed) != 0 {
		t.Fatalf("already planned: %+v", owed)
	}
}

func TestGrowWorshipRoomAddsOneAndKeepsTheRest(t *testing.T) {
	need, _ := WorshipRoomNeed(worshipIdeoligion())
	defs := furnitureDefs(map[string]Bounds{"TestAltar": {Width: 1, Height: 2}, "TestIdeogram": {Width: 1, Height: 1}})
	shape, _ := need.shape(defs)
	base := growPlan(LayoutPlan{Zones: coreTestZones()}, 6, 1, BuildTierCamp)
	grown, added, _ := growChildRoom(base, shape, nil)
	if !added || len(grown.Rooms) != len(base.Rooms)+1 {
		t.Fatalf("added=%v rooms %d -> %d", added, len(base.Rooms), len(grown.Rooms))
	}
	if _, err := CheckRoutes(grown); err != nil {
		t.Fatal(err)
	}
}

func TestWorshipRoomRoleTables(t *testing.T) {
	if role, ok := PlannedRoleFor(RoomRoleWorshipRoom); !ok || role != PlannedWorship {
		t.Fatal("the WorshipRoom role has no layout module")
	}
	if moduleRoomRoles[PlannedWorship] != RoomRoleWorshipRoom {
		t.Fatal("worship room tables")
	}
	if _, ok := InteriorTemplateFor(RoomRoleWorshipRoom); !ok {
		t.Fatal("no interior template")
	}
	if f, err := Facility(RoomRoleWorshipRoom); err != nil || f.Status != FacilityImplemented || !f.FurnitureFromGame || f.Content != "Ideology" {
		t.Fatalf("catalog row: %+v %v", f, err)
	}
}
