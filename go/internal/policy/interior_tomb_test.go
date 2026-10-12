package policy

import (
	"testing"
)

func TestTombSarcophagiLineBothSidesOfTheAisle(t *testing.T) {
	for _, size := range [][4]int32{{5, 5, 2, 10}, {5, 3, 2, 6}, {5, 4, 2, 8}, {4, 4, 1, 4}} {
		_, plan := planAround(t, RoomRoleTomb, size[0], size[1], size[2], InteriorPieceDef{})
		if len(plan.Pieces) != int(size[3]) {
			t.Errorf("%v: %d sarcophagi, want %d", size, len(plan.Pieces), size[3])
		}
		for _, p := range plan.Pieces {
			if p.Def != testSarcophagus {
				t.Errorf("%v: %s is %s, not a sarcophagus", size, p.Slot, p.Def)
			}
		}
	}
	if _, ok := PlanRoomInterior(interiorRoomsAround(RoomRoleTomb, 2, 4, 0)[0], InteriorPieceDef{}); ok {
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
