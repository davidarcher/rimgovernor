package policy

import "testing"

func TestTombSarcophagiLineTheBackWall(t *testing.T) {
	for _, size := range [][3]int32{{3, 3, 1}, {5, 4, 1}, {7, 4, 2}, {8, 6, 3}} {
		plan := assertInteriorRepeatable(t, RoomRoleTomb, size[0], size[1], size[2])
		if len(plan.Canonical) != int(size[0]) {
			t.Errorf("%v: %d sarcophagi, want one per wall cell", size, len(plan.Canonical))
		}
		for _, p := range plan.Canonical {
			if p.Def != SarcophagusDefinition || p.Row != "sarcophagi" || p.Rect.Z+p.Rect.Height != plan.Frame.Depth || p.Rect.Height != 2 {
				t.Errorf("%v: %s is %s at %+v, not a sarcophagus on the back wall", size, p.Slot, p.Def, p.Rect)
			}
		}
	}
	if _, ok := PlanInterior(interiorRoomsAround(RoomRoleTomb, 5, 2, 1)[0], InteriorPieceDef{}); ok {
		t.Error("a 2-deep tomb has no aisle")
	}
}
