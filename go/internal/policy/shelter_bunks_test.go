package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// bunkRoom is a 9x9 planned shelter on the hallway at z 30, its door in the
// hallway wall.
func bunkRoom() PlannedRoom {
	return hallRoom(PlannedShelter, 20, 30, 9, 9, true)
}

func TestPlanShelterBunksKeepsOffAisleAndStorage(t *testing.T) {
	room := bunkRoom()
	shell, err := room.Footprint()
	if err != nil {
		t.Fatal(err)
	}
	bunks := PlanShelterBunks(room, nil, testShapes, 8, 1, 0, nil)
	if len(bunks) != 8 {
		t.Fatalf("bunks %+v", bunks)
	}
	interior, forbidden := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, c := range shell.Interior() {
		interior[c] = true
	}
	for _, c := range rectCells(starterStorage(shell)) {
		forbidden[c] = true
	}
	for _, c := range DoorwayAisles(Bounds{Width: 100, Height: 100}, []SiteCell{{Cell: shell.Door(), Doorway: domain.Known(true)}}) {
		forbidden[c] = true
	}
	used := map[domain.Cell]bool{}
	for _, bunk := range bunks {
		for _, p := range rectCells(bunk.Rect) {
			if !interior[p] || forbidden[p] || used[p] {
				t.Fatalf("bunk cell %v: interior=%v forbidden=%v used=%v", p, interior[p], forbidden[p], used[p])
			}
			used[p] = true
		}
	}
}

func TestPlanShelterBunksHonoursReservedAndMinedCells(t *testing.T) {
	room := bunkRoom()
	first := PlanShelterBunks(room, nil, testShapes, 4, 1, 0, nil)
	var reserved []domain.Cell
	for _, bunk := range first {
		reserved = append(reserved, rectCells(bunk.Rect)...)
	}
	second := PlanShelterBunks(room, nil, testShapes, 8, 1, 0, reserved)
	if len(second) != 8 {
		t.Fatalf("bunks %+v", second)
	}
	held := map[domain.Cell]bool{}
	for _, c := range reserved {
		held[c] = true
	}
	for _, bunk := range second {
		for _, p := range rectCells(bunk.Rect) {
			if held[p] {
				t.Fatalf("bunk cell %v lies on a reserved cell", p)
			}
		}
	}
	// The cells plan dig still mines are kept clear alike.
	mined := PlanShelterBunks(room, reserved, testShapes, 8, 1, 0, nil)
	for _, bunk := range mined {
		for _, p := range rectCells(bunk.Rect) {
			if held[p] {
				t.Fatalf("bunk cell %v lies on a mined cell", p)
			}
		}
	}
}
