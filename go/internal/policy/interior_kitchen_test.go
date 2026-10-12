package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A kitchen holds at most InteriorTable.KitchenStoves stoves, each against a
// wall of the room and worked from a cell inside it.
func TestKitchenStovesStandAgainstAWall(t *testing.T) {
	for _, size := range [][3]int32{{6, 5, 2}, {6, 5, 0}, {5, 4, 1}, {9, 7, 3}} {
		room, plan := planAround(t, RoomRoleKitchen, size[0], size[1], size[2], InteriorPieceDef{})
		if n := len(plan.Pieces); n < 1 || n > InteriorDefs().KitchenStoves {
			t.Errorf("%v: %d stoves", size, n)
		}
		r := room.Interior
		for _, p := range plan.Pieces {
			onWall := p.Rect.X == r.X || p.Rect.Z == r.Z || p.Rect.X+p.Rect.Width == r.X+r.Width || p.Rect.Z+p.Rect.Height == r.Z+r.Height
			if i, ok := p.Interaction(); !onWall || !ok || !rectContains(r, i) {
				t.Errorf("%v: %s %+v works from %v (on a wall: %v)", size, p.Slot, p.Rect, i, onWall)
			}
		}
	}
}

func TestButcherPlacementAvoidsKitchens(t *testing.T) {
	rooms := domain.Known(RoomObservation{Shapes: testShapes, Rooms: []Room{
		{ID: "k", Role: domain.Known(RoomRoleKitchen), Cells: []domain.Cell{{X: 1, Z: 1}}, Contents: domain.Known([]Amount{})},
		{ID: "w", Role: domain.Known(RoomRoleWorkshop), Cells: []domain.Cell{{X: 2, Z: 2}}, Contents: domain.Known([]Amount{})},
	}})
	if cells := SeparationProtectedCells(rooms, true); len(cells) != 1 || cells[0] != (domain.Cell{X: 1, Z: 1}) {
		t.Fatalf("butcher placement protects %v", cells)
	}
	if cells := SeparationProtectedCells(rooms, false); len(cells) != 0 {
		t.Fatalf("cooking placement protects %v", cells)
	}
}
