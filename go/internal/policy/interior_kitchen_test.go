package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestKitchenLayoutRepeatsAcrossDoorsAndSizes(t *testing.T) {
	for _, size := range [][3]int32{{6, 5, 2}, {6, 5, 0}, {5, 4, 1}, {3, 3, 1}, {9, 7, 3}} {
		plan := assertInteriorRepeatable(t, RoomRoleKitchen, size[0], size[1], size[2])
		for _, p := range plan.Canonical {
			if p.Rot != domain.North || p.Rect.Z != size[1]-1 {
				t.Errorf("%v: %s off the back wall: %+v %s", size, p.Slot, p.Rect, p.Rot)
			}
		}
	}
}

// A kitchen's second door leads to its freezer: the stoves line that
// wall beside the door, facing into the room.
func TestKitchenStovesBesideTheFreezerDoor(t *testing.T) {
	room := Rectangle{X: 10, Z: 20, Width: 6, Height: 5}
	entrance := domain.Cell{X: 12, Z: 19}
	for _, c := range []struct {
		freezer domain.Cell
		rot     domain.Rotation
		wall    func(Rectangle) bool
	}{
		{domain.Cell{X: 16, Z: 20}, domain.East, func(r Rectangle) bool { return r.X == 15 && r.Z >= 21 }},
		{domain.Cell{X: 15, Z: 25}, domain.North, func(r Rectangle) bool { return r.Z == 24 && r.X+r.Width <= 15 }},
	} {
		plan, ok := PlanInterior(InteriorRoom{Shapes: testShapes, Role: RoomRoleKitchen, Interior: room, Doors: []domain.Cell{entrance, c.freezer}}, InteriorPieceDef{})
		if !ok || len(plan.Pieces) == 0 {
			t.Fatalf("freezer door %v: no plan", c.freezer)
		}
		assertInteriorRegular(t, plan)
		for _, p := range plan.Pieces {
			if p.Def != testStove || p.Rot != c.rot || !c.wall(p.Rect) {
				t.Errorf("freezer door %v: %s %+v %s", c.freezer, p.Slot, p.Rect, p.Rot)
			}
			if i, ok := p.Interaction(); !ok || !rectContains(room, i) {
				t.Errorf("freezer door %v: %s works from %v", c.freezer, p.Slot, i)
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
