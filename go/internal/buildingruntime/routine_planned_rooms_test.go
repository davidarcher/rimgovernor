package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestPlannedRoomOwedOnlyFromMasonryUntilTheRoomStands(t *testing.T) {
	kitchen := policy.LayoutRoom{Role: policy.ModuleKitchen, Interior: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 9}}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.LayoutRoom{kitchen}}), Rooms: domain.Known(policy.RoomObservation{}), BuildTier: domain.Known(policy.BuildTierCamp)}
	if _, owed := plannedRoomOwed(facts, policy.ModuleKitchen); owed {
		t.Fatal("a Camp colony shelled the planned kitchen")
	}
	facts.BuildTier = domain.Known(policy.BuildTierMasonry)
	if r, owed := plannedRoomOwed(facts, policy.ModuleKitchen); !owed || r.Interior != kitchen.Interior {
		t.Fatal("the planned kitchen is not owed at Masonry", r, owed)
	}
	if plannedRoomCells(facts, policy.ModuleKitchen) != nil {
		t.Fatal("an unbuilt kitchen restricted the stove")
	}
	facts.Rooms = domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "k", Cells: []domain.Cell{{X: 13, Z: 12}}, Enclosed: domain.Known(true)}}})
	if _, owed := plannedRoomOwed(facts, policy.ModuleKitchen); owed {
		t.Fatal("a standing kitchen is still owed")
	}
	if cells := plannedRoomCells(facts, policy.ModuleKitchen); len(cells) != 30 {
		t.Fatal("the stove is not held to the kitchen interior", len(cells))
	}
}
