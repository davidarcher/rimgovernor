package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func bunkLayout(t *testing.T, shell domain.RoomFootprint, err error) StarterLayout {
	t.Helper()
	if err != nil {
		t.Fatal(err)
	}
	b := shell.Bounds()
	return StarterLayout{Room: Rectangle{b.X, b.Z, b.Width, b.Height}, Storage: starterStorage(shell), Shell: shell}
}

func TestPlanShelterBunksKeepsOffAisleAndStorage(t *testing.T) {
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: 10, Z: 10, Width: 9, Height: 9}, domain.South)
	layout := bunkLayout(t, shell, err)
	bunks := PlanShelterBunks(layout, testShapes, 8, 1, 0, nil)
	if len(bunks) != 8 {
		t.Fatalf("bunks %+v", bunks)
	}
	interior, forbidden := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, c := range shell.Interior() {
		interior[c] = true
	}
	for _, c := range rectCells(layout.Storage) {
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

func TestPlanShelterBunksHonoursReservedCells(t *testing.T) {
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: 10, Z: 10, Width: 9, Height: 9}, domain.South)
	layout := bunkLayout(t, shell, err)
	first := PlanShelterBunks(layout, testShapes, 4, 1, 0, nil)
	var reserved []domain.Cell
	for _, bunk := range first {
		reserved = append(reserved, rectCells(bunk.Rect)...)
	}
	second := PlanShelterBunks(layout, testShapes, 8, 1, 0, reserved)
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
}

func TestBunkLayoutPrefersTheShellAroundTheBunks(t *testing.T) {
	near, err := domain.RectangleFootprint(domain.RoomBounds{X: 10, Z: 10, Width: 9, Height: 9}, domain.South)
	far, err2 := domain.RectangleFootprint(domain.RoomBounds{X: 30, Z: 30, Width: 9, Height: 9}, domain.South)
	a, b := bunkLayout(t, near, err), bunkLayout(t, far, err2)
	var rects []Rectangle
	for _, bunk := range PlanShelterBunks(b, testShapes, 3, 1, 0, nil) {
		rects = append(rects, bunk.Rect)
	}
	chosen, ok := BunkLayout([]StarterLayout{a, b}, rects)
	if !ok || chosen.Room != b.Room {
		t.Fatalf("chose %+v ok=%v", chosen.Room, ok)
	}
	// A bunk outside the interior disqualifies the layout.
	if _, ok := BunkLayout([]StarterLayout{a}, []Rectangle{{X: 10, Z: 11, Width: 1, Height: 2}}); ok {
		t.Fatal("a bunk on the wall fitted the rectangle")
	}
}
