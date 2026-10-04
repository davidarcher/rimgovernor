package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// wingTestPlan is a hallway with nothing else, over core ground g.
func wingTestPlan(spine []SpineSegment, wings []Wing) LayoutPlan {
	return LayoutPlan{Spine: spine, Entrances: spineEntrances(spine), Wings: wings}
}

// checkWingInvariants asserts what any wing must hold whatever its axis:
// rooms apart, every door on the corridor, the plan valid and routable.
func checkWingInvariants(t *testing.T, plan LayoutPlan) {
	t.Helper()
	if !plan.Valid() {
		t.Fatal("plan invalid")
	}
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
	for _, w := range plan.Wings {
		f := frameOf(w)
		floor := f.corridorTo(f.reachV(w.Rooms))
		for i, r := range w.Rooms {
			cut := false
			for _, c := range rectCells(floor) {
				if c == (domain.Cell{X: r.Door.X + 1, Z: r.Door.Z}) || c == (domain.Cell{X: r.Door.X - 1, Z: r.Door.Z}) ||
					c == (domain.Cell{X: r.Door.X, Z: r.Door.Z + 1}) || c == (domain.Cell{X: r.Door.X, Z: r.Door.Z - 1}) {
					cut = true
				}
			}
			if !cut {
				t.Fatalf("room %d door %v is off the corridor", i, r.Door)
			}
			for _, o := range w.Rooms[i+1:] {
				if rectsOverlap(r.Interior, o.Interior) {
					t.Fatalf("rooms overlap: %v %v", r.Interior, o.Interior)
				}
			}
		}
	}
}

func TestWingsRunAlongEitherAxis(t *testing.T) {
	size := WingRoomSize(BuildTierCamp)
	// A hallway along X with ground south of it only, and its transpose.
	horizontal := []SpineSegment{{From: domain.Cell{X: 40, Z: 40}, To: domain.Cell{X: 60, Z: 40}}}
	vertical := []SpineSegment{{From: domain.Cell{X: 40, Z: 40}, To: domain.Cell{X: 40, Z: 60}}}
	var counts []int
	var sizes [][2]int32
	for _, spine := range [][]SpineSegment{horizontal, vertical} {
		g := openGround(30, 30, 60, 60)
		_, wings, ok := g.siteBedWing(spine, nil, nil, size, nil)
		if !ok || len(wings) != 1 {
			t.Fatal("no wing sited", spine)
		}
		checkWingInvariants(t, wingTestPlan(spine, wings))
		f := frameOf(wings[0])
		if f.horiz != !alongX(spine[0]) {
			t.Fatalf("wing orientation %v off hallway %v", f.horiz, spine[0])
		}
		counts = append(counts, len(wings[0].Rooms))
		sizes = append(sizes, f.size)
	}
	if counts[0] != counts[1] || counts[0] != wingMaxRooms || sizes[0] != sizes[1] || sizes[0] != size {
		t.Fatal("twins differ", counts, sizes)
	}
}

func TestWingsFitNarrowAndWideStrips(t *testing.T) {
	size := WingRoomSize(BuildTierCamp)
	for name, tc := range map[string]struct {
		g     coreGrid
		spine []SpineSegment
	}{
		// A tall narrow strip with a vertical hallway: wings run sideways.
		"tall": {openGround(0, 0, 36, 120), []SpineSegment{{From: domain.Cell{X: 2, Z: 10}, To: domain.Cell{X: 2, Z: 100}}}},
		// A wide strip with a horizontal hallway: wings run up and down.
		"wide": {openGround(0, 0, 120, 36), []SpineSegment{{From: domain.Cell{X: 10, Z: 2}, To: domain.Cell{X: 100, Z: 2}}}},
	} {
		spine, wings := tc.g.siteBedWings(tc.spine, nil, nil, 20, BuildTierCamp)
		if len(wings) == 0 {
			t.Fatal(name, "no wing sited")
		}
		got := 0
		for _, w := range wings {
			got += len(w.Rooms)
			if len(w.Rooms) > wingMaxRooms || frameOf(w).size != size {
				t.Fatal(name, "wing", len(w.Rooms), frameOf(w).size)
			}
		}
		if got < 10 {
			t.Fatal(name, "housed", got)
		}
		checkWingInvariants(t, wingTestPlan(spine, wings))
	}
}

func TestSuiteBlocksRunAlongEitherAxis(t *testing.T) {
	targets := []float64{40, 40, 40, 40, 40, 40, 40, 40}
	for _, spine := range [][]SpineSegment{
		{{From: domain.Cell{X: 40, Z: 40}, To: domain.Cell{X: 60, Z: 40}}},
		{{From: domain.Cell{X: 40, Z: 40}, To: domain.Cell{X: 40, Z: 60}}},
	} {
		g := openGround(0, 0, 120, 120)
		spine, wings := g.siteSuiteBlocks(spine, nil, nil, targets)
		if len(wings) == 0 {
			t.Fatal("no suite block")
		}
		for _, w := range wings {
			if len(w.Rooms) > suiteMaxRooms {
				t.Fatal("block too big", len(w.Rooms))
			}
		}
		checkWingInvariants(t, wingTestPlan(spine, wings))
	}
}
