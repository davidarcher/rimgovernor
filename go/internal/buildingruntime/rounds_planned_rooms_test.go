package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The initial shelter tries the plan's shelter room at Camp, not the storage room.
func TestStarterShellPlansTheShelterAtCamp(t *testing.T) {
	storage := policy.PlannedRoom{Role: policy.PlannedShelter, Interior: policy.Rectangle{X: 116, Z: 130, Width: 9, Height: 7}, Door: domain.Cell{X: 120, Z: 129}, DoorRot: domain.South}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.PlannedRoom{storage}}), BuildTier: domain.Known(policy.BuildTierCamp)}
	starter := &RoundsBuildingPlanner{shelter: true, phase: policy.HousingShelter}
	shells := starter.shellPlan(facts)
	want, _ := storage.Footprint()
	if len(shells) != 1 || !domain.SameRoomFootprint(shells[0], want) || shells[0].Door() != storage.Door {
		t.Fatalf("starter shells %v, want the storage room", shells)
	}
}

func TestPlannedRoomOwedAtAnyTierUntilTheRoomStands(t *testing.T) {
	kitchen := policy.PlannedRoom{Role: policy.PlannedKitchen, Interior: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 9}}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.PlannedRoom{kitchen}}), Rooms: domain.Known(policy.RoomObservation{Shapes: testPieceShapes}), BuildTier: domain.Known(policy.BuildTierCamp)}
	for _, tier := range []policy.BuildTier{policy.BuildTierCamp, policy.BuildTierMasonry} {
		facts.BuildTier = domain.Known(tier)
		if r, owed := plannedRoomOwed(facts, policy.PlannedKitchen); !owed || r.Interior != kitchen.Interior {
			t.Fatal("the planned kitchen is not owed at tier", tier, r, owed)
		}
	}
	if plannedRoomCells(facts, policy.PlannedKitchen) != nil {
		t.Fatal("an unbuilt kitchen restricted the stove")
	}
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{{ID: "k", Cells: []domain.Cell{{X: 13, Z: 12}}, Enclosed: domain.Known(true)}}})
	if _, owed := plannedRoomOwed(facts, policy.PlannedKitchen); owed {
		t.Fatal("a standing kitchen is still owed")
	}
	if cells := plannedRoomCells(facts, policy.PlannedKitchen); len(cells) != 30 {
		t.Fatal("the stove is not held to the kitchen interior", len(cells))
	}
}

func TestPlannedRoomInteriorIsTheWholeRoom(t *testing.T) {
	room := policy.PlannedRoom{Role: policy.PlannedKitchen, Interior: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 5}}
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
	table := &RoundsBuildingPlanner{concern: policy.EnsureComfort, facility: &dining, definition: "Table1x2c"}
	facts := observation.ColonyProjection{}
	for name, r := range map[string]*RoundsBuildingPlanner{
		"rooms unread":  table,
		"not a comfort": {concern: policy.EnsureCooking, facility: &dining, definition: "Table1x2c"},
		"a recreation":  {concern: policy.EnsureComfort, facility: &dining, definition: "HorseshoesPin"},
		"no facility":   {concern: policy.EnsureComfort, definition: "Table1x2c"},
	} {
		got, _, done, err := r.plannedDiningFurnishing(context.TODO(), context.TODO(), ControlState{}, store.Rounds{}, store.StandardState{}, observation.ColonyReading{}, facts)
		if err != nil || done || got != r {
			t.Errorf("%s: planner changed or stepped: done=%v err=%v", name, done, err)
		}
	}
}
