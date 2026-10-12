package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWorkshopPlansAcrossSizes(t *testing.T) {
	for _, size := range [][3]int32{{7, 5, 1}, {7, 5, 3}, {3, 3, 1}, {8, 4, 0}, {11, 6, 2}, {12, 3, 5}} {
		planAround(t, RoomRoleWorkshop, size[0], size[1], size[2], InteriorPieceDef{})
	}
	if _, ok := PlanRoomInterior(InteriorRoom{Shapes: testShapes, Role: RoomRoleWorkshop, Interior: Rectangle{X: 0, Z: 0, Width: 7, Height: 2}, Doors: []domain.Cell{{X: 1, Z: -1}}}, InteriorPieceDef{}); ok {
		t.Error("a two-deep room planned a workshop")
	}
}

func TestWorkshopCabinetsServeTwoBenchesAndShelvesKeepTheRole(t *testing.T) {
	room, plan := planAround(t, RoomRoleWorkshop, 11, 5, 1, InteriorPieceDef{})
	var benches, cabinets, shelves []InteriorPiece
	for _, p := range plan.Pieces {
		switch p.Def {
		case testWorkshop:
			benches = append(benches, p)
		case testFurniture.Cabinet.Def:
			cabinets = append(cabinets, p)
		case workshopShelfDef:
			shelves = append(shelves, p)
		}
	}
	if len(benches) != 3 || len(cabinets) != 2 || len(shelves) != 3 {
		t.Fatalf("benches %d cabinets %d shelves %d, want 3 2 3", len(benches), len(cabinets), len(shelves))
	}
	// Each cabinet feeds the two benches beside it: every link is served.
	if served, wanted := bedroomLinks(room, plan); wanted < 1 || served != wanted {
		t.Errorf("the cabinets serve %d of %d links", served, wanted)
	}
	// RoomRoleWorker_Storeroom scores 1 per shelf, Workshop 27 per bench.
	if len(shelves) >= 27*len(benches) {
		t.Error("shelves outscore benches")
	}
}
