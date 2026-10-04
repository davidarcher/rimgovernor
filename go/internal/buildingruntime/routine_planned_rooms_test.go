package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The initial shelter tries the plan's barracks at Camp, not the storage room.
func TestStarterShellPlansTheBarracksAtCamp(t *testing.T) {
	storage := policy.LayoutRoom{Role: policy.ModuleBarracks, Interior: policy.Rectangle{X: 116, Z: 130, Width: 9, Height: 7}, Door: domain.Cell{X: 120, Z: 129}, DoorRot: domain.South}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.LayoutRoom{storage}}), BuildTier: domain.Known(policy.BuildTierCamp)}
	starter := &RoutineBuildingPlanner{shelter: true, phase: policy.HousingShelter}
	shells := starter.shellPlan(facts)
	want, _ := storage.Footprint()
	if len(shells) != 1 || !domain.SameRoomFootprint(shells[0], want) || shells[0].Door() != storage.Door {
		t.Fatalf("starter shells %v, want the storage room", shells)
	}
}

func TestPlannedRoomOwedAtAnyTierUntilTheRoomStands(t *testing.T) {
	kitchen := policy.LayoutRoom{Role: policy.ModuleKitchen, Interior: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 9}}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.LayoutRoom{kitchen}}), Rooms: domain.Known(policy.RoomObservation{Shapes: testPieceShapes}), BuildTier: domain.Known(policy.BuildTierCamp)}
	for _, tier := range []policy.BuildTier{policy.BuildTierCamp, policy.BuildTierMasonry} {
		facts.BuildTier = domain.Known(tier)
		if r, owed := plannedRoomOwed(facts, policy.ModuleKitchen); !owed || r.Interior != kitchen.Interior {
			t.Fatal("the planned kitchen is not owed at tier", tier, r, owed)
		}
	}
	if plannedRoomCells(facts, policy.ModuleKitchen) != nil {
		t.Fatal("an unbuilt kitchen restricted the stove")
	}
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{{ID: "k", Cells: []domain.Cell{{X: 13, Z: 12}}, Enclosed: domain.Known(true)}}})
	if _, owed := plannedRoomOwed(facts, policy.ModuleKitchen); owed {
		t.Fatal("a standing kitchen is still owed")
	}
	if cells := plannedRoomCells(facts, policy.ModuleKitchen); len(cells) != 30 {
		t.Fatal("the stove is not held to the kitchen interior", len(cells))
	}
}

func TestPlannedRoomInteriorIsTheWholeRoom(t *testing.T) {
	room := policy.LayoutRoom{Role: policy.ModuleKitchen, Interior: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 5}}
	if cells := plannedRoomInterior(room); len(cells) != 30 || cells[0] != (domain.Cell{X: 10, Z: 10}) || cells[29] != (domain.Cell{X: 15, Z: 14}) {
		t.Fatal(cells)
	}
}

// Dining furniture goes in the planned dining room only while the plan has
// one and no standing room hosts dining; anything else is left to the usual
// placement.
func TestPlannedDiningFurnishingLeavesOtherPlansAlone(t *testing.T) {
	t.Parallel()
	dining, err := policy.Facility(policy.RoomRoleDiningRoom)
	if err != nil {
		t.Fatal(err)
	}
	table := &RoutineBuildingPlanner{goal: policy.EnsureComfort, facility: &dining, definition: "Table1x2c"}
	facts := observation.ColonyProjection{}
	for name, r := range map[string]*RoutineBuildingPlanner{
		"rooms unread":  table,
		"not a comfort": {goal: policy.EnsureCooking, facility: &dining, definition: "Table1x2c"},
		"a recreation":  {goal: policy.EnsureComfort, facility: &dining, definition: "HorseshoesPin"},
		"no facility":   {goal: policy.EnsureComfort, definition: "Table1x2c"},
	} {
		got, _, done, err := r.plannedDiningFurnishing(context.TODO(), context.TODO(), ControlState{}, store.Rounds{}, store.GoalState{}, observation.ColonyReading{}, facts)
		if err != nil || done || got != r {
			t.Errorf("%s: planner changed or stepped: done=%v err=%v", name, done, err)
		}
	}
}
