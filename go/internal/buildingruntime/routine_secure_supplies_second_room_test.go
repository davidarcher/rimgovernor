package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// #1772: a further storage room is raised by the storeroom step while the
// first stands; with the first unbuilt the first is raised first.
func TestPlannedStorageRoomRaisesTheFurtherRoomOnceTheFirstStands(t *testing.T) {
	first := policy.Rectangle{X: 11, Z: 21, Width: 4, Height: 4}
	second := policy.Rectangle{X: 31, Z: 21, Width: 4, Height: 4}
	plan := policy.LayoutPlan{Rooms: []policy.LayoutRoom{
		{Role: policy.ModuleStorage, Interior: first, Door: domain.Cell{X: 12, Z: 20}, DoorRot: domain.South},
		{Role: policy.ModuleStorage, Interior: second, Door: domain.Cell{X: 32, Z: 20}, DoorRot: domain.South},
	}}
	shells := plan.PlannedShells(policy.RoomRoleStoreroom)
	if len(shells) != 2 {
		t.Fatal(shells)
	}
	projection := observation.ColonyProjection{LayoutPlan: domain.Known(plan)}
	var cells []policy.SiteCell
	ring := func(room domain.RoomFootprint) {
		for _, w := range room.Walls() {
			cells = append(cells, policy.SiteCell{Cell: w, PlayerEdifice: domain.Known("Wall"), Doorway: domain.Known(false)})
		}
		projection.Cells = cells
	}
	if room, ok := plannedStorageRoom(projection); !ok || room.Door() != shells[0].Door() {
		t.Fatalf("nothing stands, want the first room: %v %v", room.Door(), ok)
	}
	ring(shells[0])
	if room, ok := plannedStorageRoom(projection); !ok || room.Door() != shells[1].Door() {
		t.Fatalf("first stands, want the further room: %v %v", room.Door(), ok)
	}
	ring(shells[1])
	if room, ok := plannedStorageRoom(projection); !ok || room.Door() != shells[0].Door() {
		t.Fatalf("both stand, want the first room zoned: %v %v", room.Door(), ok)
	}
}
