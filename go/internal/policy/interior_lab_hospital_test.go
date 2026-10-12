package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLaboratoryBenchesFaceTheFloor(t *testing.T) {
	for _, size := range [][3]int32{{3, 4, 1}, {5, 4, 1}, {7, 5, 2}, {11, 6, 3}} {
		room, plan := planAround(t, RoomRoleLaboratory, size[0], size[1], size[2], InteriorPieceDef{})
		var benches []InteriorPiece
		for _, p := range plan.Pieces {
			if p.Def != testFurniture.Analyzer.Def {
				benches = append(benches, p)
			}
		}
		if want := RowCapacity(size[0], 3, 1); int32(len(benches)) != want {
			t.Errorf("%v: %d benches, want %d", size, len(benches), want)
		}
		used := map[domain.Cell]bool{}
		for _, p := range plan.Pieces {
			for _, c := range rectCells(p.Rect) {
				used[c] = true
			}
		}
		for _, p := range benches {
			if p.Def != testResearch {
				t.Errorf("%v: %s is %s", size, p.Slot, p.Def)
			}
			c, ok := p.Interaction()
			if !ok || used[c] || !rectContains(room.Interior, c) {
				t.Errorf("%v: %s interaction %v not on open floor", size, p.Slot, c)
			}
		}
	}
	if _, ok := PlanRoomInterior(interiorRoomsAround(RoomRoleLaboratory, 5, 3, 1)[0], InteriorPieceDef{}); ok {
		t.Error("a 3-deep lab has no interaction row")
	}
}

func TestHospitalHoldsBedPairsAndTheirMonitors(t *testing.T) {
	for _, size := range [][3]int32{{3, 3, 1}, {5, 4, 1}, {7, 4, 2}, {8, 5, 3}} {
		_, plan := planAround(t, RoomRoleHospital, size[0], size[1], size[2], InteriorPieceDef{})
		var beds, monitors []InteriorPiece
		for _, p := range plan.Pieces {
			if p.Def == testFurniture.Monitor.Def {
				monitors = append(monitors, p)
			} else {
				beds = append(beds, p)
			}
		}
		if len(beds) == 0 || len(beds) != 2*len(monitors) {
			t.Fatalf("%v: %d beds, %d monitors", size, len(beds), len(monitors))
		}
	}
}
