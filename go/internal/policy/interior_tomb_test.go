package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestTombSarcophagiLineBothSidesOfTheAisle(t *testing.T) {
	for _, size := range [][4]int32{{5, 5, 2, 10}, {5, 3, 2, 6}, {5, 4, 2, 8}, {4, 4, 1, 4}} {
		plan := assertInteriorRepeatable(t, RoomRoleTomb, size[0], size[1], size[2])
		if len(plan.Canonical) != int(size[3]) {
			t.Errorf("%v: %d sarcophagi, want %d", size, len(plan.Canonical), size[3])
		}
		aisle := plan.Frame.Entrance
		for _, p := range plan.Canonical {
			if p.Def != testSarcophagus || p.Rect.Width != 2 || p.Rect.Height != 1 {
				t.Errorf("%v: %s is %s at %+v, not a sarcophagus across the aisle", size, p.Slot, p.Def, p.Rect)
			}
			west := p.Rect.X+p.Rect.Width == aisle && p.Rot == domain.West
			east := p.Rect.X == aisle+1 && p.Rot == domain.East
			if !west && !east {
				t.Errorf("%v: %s at %+v %s does not flank the aisle at %d", size, p.Slot, p.Rect, p.Rot, aisle)
			}
		}
	}
	if _, ok := PlanInterior(interiorRoomsAround(RoomRoleTomb, 2, 4, 0)[0], InteriorPieceDef{}); ok {
		t.Error("a 2-wide tomb has no side for a sarcophagus")
	}
}

func TestAisleRowsStepByPitch(t *testing.T) {
	if got := AisleRows(7, 2); len(got) != 4 || got[3] != 6 {
		t.Errorf("battery rows %v", got)
	}
	if got := AisleRows(5, 1); len(got) != 5 {
		t.Errorf("tomb rows %v", got)
	}
}
