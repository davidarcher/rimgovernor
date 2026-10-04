package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSuiteSizeSnapsTheTargetsSpace(t *testing.T) {
	for _, c := range []struct {
		target float64
		w, d   int32
	}{
		{0, 3, 4},                                // nothing asked: the smallest
		{ImpressivenessDull, 4, 6},               // 22 cells
		{ImpressivenessDecent, 5, 8},             // 40 cells
		{ImpressivenessSlightlyImpressive, 7, 7}, // 49 cells
		{1000, suiteMaxWidth, suiteMaxDepth},     // unreachable: the largest
	} {
		if w, d := SuiteSize(c.target); w != c.w || d != c.d {
			t.Errorf("SuiteSize(%v) = %dx%d, want %dx%d", c.target, w, d, c.w, c.d)
		}
	}
	// A higher target never gets a smaller suite.
	prev := int32(0)
	for target := 0.0; target <= 80; target++ {
		w, d := SuiteSize(target)
		if w < suiteMinWidth || w > suiteMaxWidth || d < suiteMinDepth || d > suiteMaxDepth || w*d < prev {
			t.Fatalf("SuiteSize(%v) = %dx%d after area %d", target, w, d, prev)
		}
		prev = w * d
	}
}

func testSuiteWing(t *testing.T, p LayoutPlan) Wing {
	t.Helper()
	i := wingOf(p.Wings, WingSuites)
	if i < 0 {
		t.Fatalf("no suite wing: %+v", p.Wings)
	}
	return p.Wings[i]
}

func TestSuiteWingOnlyOnceASuiteIsWanted(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	if wingOf(p.Wings, WingSuites) >= 0 {
		t.Fatal("suite wing with no suite wanted")
	}
	if g := growPlan(p, 3, 1, BuildTierCamp); wingOf(g.Wings, WingSuites) >= 0 {
		t.Fatal("suite wing grown with no suite wanted")
	}
}

func TestSuiteWingIsSeparateFromTheStandardWing(t *testing.T) {
	targets := []float64{ImpressivenessSlightlyImpressive, ImpressivenessDull, ImpressivenessDecent}
	p := growPlan(corePlan(coreTestZones(), 3, BuildTierCamp), 3, 1, BuildTierCamp, targets...)
	std := checkWing(t, p)
	suites := testSuiteWing(t, p)
	if len(suites.Rooms) != len(targets) {
		t.Fatalf("suites %d, want %d", len(suites.Rooms), len(targets))
	}
	if suites.Corridor.From == std.Corridor.From {
		t.Fatal("suite wing shares the standard wing's corridor")
	}
	corridor := spineRects([]SpineSegment{suites.Corridor})[0]
	step := map[domain.Rotation]domain.Cell{domain.East: {X: 1}, domain.West: {X: -1}}
	for i, r := range suites.Rooms {
		if r.Role != ModuleSuite {
			t.Fatal("suite role", r.Role)
		}
		// Sized by target; width runs along the (north-south) corridor.
		if w, d := SuiteSize(targets[i]); r.Interior.Height != w || r.Interior.Width != d {
			t.Fatalf("suite %d is %dx%d, want %dx%d", i, r.Interior.Height, r.Interior.Width, w, d)
		}
		s := step[r.DoorRot]
		if !contains(corridor, domain.Cell{X: r.Door.X + s.X, Z: r.Door.Z + s.Z}) {
			t.Fatalf("suite door %v off the corridor %+v", r.Door, corridor)
		}
		for _, o := range std.Rooms {
			if rectsOverlap(roomWalls(r), o.Interior) {
				t.Fatal("suite overlaps a standard room", r, o)
			}
		}
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal("routes:", err)
	}
	if in, ok := InteriorRoomFromLayout(suites.Rooms[0], testShapes); !ok || in.Role != RoomRoleSuite {
		t.Fatal("suite interior role", in, ok)
	}
	if m, ok := LayoutModule(RoomRoleSuite); !ok || m != ModuleSuite {
		t.Fatal("LayoutModule(Suite)", m, ok)
	}
}
