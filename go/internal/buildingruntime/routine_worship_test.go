package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recorded ideology read: one building precept that took an altar. The def
// names are fixture data; the planner reads whatever the game names.
func worshipProjection(standing bool) (observation.ColonyProjection, policy.LayoutRoom) {
	room := policy.LayoutRoom{Role: policy.ModuleWorship, Interior: policy.Rectangle{X: 10, Z: 20, Width: 4, Height: 4}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	var rooms policy.RoomObservation
	if standing {
		var cells []domain.Cell
		for z := room.Interior.Z; z < room.Interior.Z+room.Interior.Height; z++ {
			for x := room.Interior.X; x < room.Interior.X+room.Interior.Width; x++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
		rooms.Rooms = []policy.Room{{ID: "r1", Enclosed: domain.Known(true), Cells: cells}}
	}
	facts := observation.ColonyProjection{
		LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.LayoutRoom{room}}),
		Rooms:      domain.Known(rooms),
		Definitions: []observation.PlanningDefinition{{
			Name: "TestAltar", Available: domain.Known(true), Size: domain.Known(policy.Bounds{Width: 1, Height: 2}),
		}},
	}
	facts.Facts.Ideology = domain.Known(policy.Ideoligion{Facts: policy.IdeoligionFacts{
		Buildings: []policy.HeldBuilding{{ID: "1", Def: "Precept_Altar", Building: "TestAltar"}},
	}})
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	facts.Facts.Sleeping = domain.Known(policy.SleepingObservation{})
	return facts, room
}

// The worship room rides under MaintainHousing with the child rooms: shelled,
// then furnished with the required building, each owing the bedroom phase;
// an unknown ideoligion owes nothing.
func TestWorshipRoomOwesHousingUntilTheBuildingsStand(t *testing.T) {
	facts, room := worshipProjection(false)
	if step := childRoomStep(facts); step.Kind != policy.ChildRoomShell || !step.Room.Same(room) {
		t.Fatalf("unbuilt room: %+v", step)
	}
	if owed, known := bedroomsOwed(facts, policy.StageReserves).Value(); !known || !owed {
		t.Fatalf("the shell is owed: %v %v", owed, known)
	}
	facts, _ = worshipProjection(true)
	step := childRoomStep(facts)
	if step.Kind != policy.ChildRoomPlace || step.Piece.Def != "TestAltar" {
		t.Fatalf("standing room: %+v", step)
	}
	altar, err := domain.NewBuilding("TestAltar", step.Piece.Anchor(), step.Piece.Rot, "")
	if err != nil {
		t.Fatal(err)
	}
	cells := []domain.Cell{{X: step.Piece.Rect.X, Z: step.Piece.Rect.Z}, {X: step.Piece.Rect.X, Z: step.Piece.Rect.Z + 1}}
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "a1", Building: altar, Cells: cells}}})
	if step := childRoomStep(facts); step.Owed() {
		t.Fatalf("altar standing: %+v", step)
	}
	facts.Facts.Ideology = domain.Unknown[policy.Ideoligion]()
	if step := childRoomStep(facts); step.Kind != policy.ChildRoomNone {
		t.Fatalf("unknown ideoligion owes no room: %+v", step)
	}
	if len(worshipDefinitions(facts)) != 0 {
		t.Fatal("unknown ideoligion names no building")
	}
}

// A review names the required buildings for the planners' own reads.
func TestReviewRemembersTheRequiredBuildings(t *testing.T) {
	r := &Rounder{}
	facts, _ := worshipProjection(false)
	r.reviewChildRooms(&observation.RoutineReading{ColonyReading: observation.ColonyReading{Projection: facts}})
	if got := r.census.rememberedWorship(); len(got) != 1 || got[0] != "TestAltar" {
		t.Fatalf("remembered %v", got)
	}
}
