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

func TestShellCornerCellsRectangle(t *testing.T) {
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: 10, Z: 10, Width: 9, Height: 9}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	got := ShellCornerCells(shell)
	want := []domain.Cell{{X: 11, Z: 11}, {X: 17, Z: 11}, {X: 11, Z: 17}, {X: 17, Z: 17}}
	if len(got) != len(want) {
		t.Fatalf("corners %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("corners %v, want %v", got, want)
		}
	}
}

func TestPlanShelterBunksKeepsBedsOffCornersAisleAndStorage(t *testing.T) {
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: 10, Z: 10, Width: 9, Height: 9}, domain.South)
	layout := bunkLayout(t, shell, err)
	bunks := PlanShelterBunks(layout, 8, 8, nil)
	if len(bunks.Beds) != 8 {
		t.Fatalf("beds %v", bunks.Beds)
	}
	if len(bunks.Spots) == 0 {
		t.Fatal("no spots fit beside the beds")
	}
	interior, corner, forbidden := map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, c := range shell.Interior() {
		interior[c] = true
	}
	for _, c := range ShellCornerCells(shell) {
		corner[c] = true
	}
	for _, c := range rectCells(layout.Storage) {
		forbidden[c] = true
	}
	for _, c := range DoorwayAisles(Bounds{Width: 100, Height: 100}, []SiteCell{{Cell: shell.Door(), Doorway: domain.Known(true)}}) {
		forbidden[c] = true
	}
	used := map[domain.Cell]bool{}
	for _, anchor := range bunks.Beds {
		for _, p := range BunkFootprint(anchor) {
			if !interior[p] || corner[p] || forbidden[p] || used[p] {
				t.Fatalf("bed cell %v: interior=%v corner=%v forbidden=%v used=%v", p, interior[p], corner[p], forbidden[p], used[p])
			}
			used[p] = true
		}
	}
	for _, anchor := range bunks.Spots {
		for _, p := range BunkFootprint(anchor) {
			if !interior[p] || forbidden[p] || used[p] {
				t.Fatalf("spot cell %v: interior=%v forbidden=%v used=%v", p, interior[p], forbidden[p], used[p])
			}
			used[p] = true
		}
	}
}

func TestPlanShelterBunksHonoursBlockedCells(t *testing.T) {
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: 10, Z: 10, Width: 9, Height: 9}, domain.South)
	layout := bunkLayout(t, shell, err)
	first := PlanShelterBunks(layout, 0, 4, nil)
	var blocked []domain.Cell
	for _, anchor := range first.Spots {
		f := BunkFootprint(anchor)
		blocked = append(blocked, f[0], f[1])
	}
	second := PlanShelterBunks(layout, 8, 0, blocked)
	if len(second.Beds) != 8 {
		t.Fatalf("beds %v", second.Beds)
	}
	held := map[domain.Cell]bool{}
	for _, c := range blocked {
		held[c] = true
	}
	for _, anchor := range second.Beds {
		for _, p := range BunkFootprint(anchor) {
			if held[p] {
				t.Fatalf("bed cell %v lies on a spot", p)
			}
		}
	}
}

func TestPlanShelterBunksHutHoldsEightBeds(t *testing.T) {
	for i, shell := range HutTemplateShells(domain.Cell{X: 40, Z: 40}) {
		layout := bunkLayout(t, shell, nil)
		bunks := PlanShelterBunks(layout, 8, 8, nil)
		// The circle and the medium ovals house the tribal eight; the
		// small circle and the low ovals house what fits.
		if i < 5 && len(bunks.Beds) != 8 {
			t.Fatalf("hut template %d: beds %v", i, bunks.Beds)
		}
		if len(bunks.Beds) == 0 {
			t.Fatalf("hut template %d: no bed fits", i)
		}
	}
}

func TestBunkLayoutPrefersTheShellAroundTheBunks(t *testing.T) {
	near, err := domain.RectangleFootprint(domain.RoomBounds{X: 10, Z: 10, Width: 9, Height: 9}, domain.South)
	far, err2 := domain.RectangleFootprint(domain.RoomBounds{X: 30, Z: 30, Width: 9, Height: 9}, domain.South)
	a, b := bunkLayout(t, near, err), bunkLayout(t, far, err2)
	bunks := PlanShelterBunks(b, 2, 1, nil)
	chosen, ok := BunkLayout([]StarterLayout{a, b}, bunks.Beds, bunks.Spots)
	if !ok || chosen.Room != b.Room {
		t.Fatalf("chose %+v ok=%v", chosen.Room, ok)
	}
	// A bed on a corner cell disqualifies the layout.
	if _, ok := BunkLayout([]StarterLayout{a}, []domain.Cell{{X: 11, Z: 11}}, nil); ok {
		t.Fatal("a corner bed fitted the rectangle")
	}
	if _, ok := BunkLayout([]StarterLayout{a}, nil, []domain.Cell{{X: 11, Z: 11}}); !ok {
		t.Fatal("a corner spot did not fit the rectangle")
	}
}
