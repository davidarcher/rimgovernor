package policy

import (
	"math/rand"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestCollidesIsTheAxisGapRule keeps the inlined collides equal to its
// definition: no wall cell between the interiors on both axes.
func TestCollidesIsTheAxisGapRule(t *testing.T) {
	rng := rand.New(rand.NewSource(3012))
	rect := func() Rectangle {
		return Rectangle{X: int32(rng.Intn(30)), Z: int32(rng.Intn(30)), Width: int32(1 + rng.Intn(9)), Height: int32(1 + rng.Intn(9))}
	}
	for n := 0; n < 20000; n++ {
		a, b := rect(), rect()
		if want := axisGap(a.X, a.Width, b.X, b.Width) < 1 && axisGap(a.Z, a.Height, b.Z, b.Height) < 1; collides(a, b) != want {
			t.Fatalf("collides(%+v, %+v) = %v, want %v", a, b, !want, want)
		}
	}
}

// TestDenseCoreColumnMatchesCoreGrid keeps denseCore.column equal to
// buildGround.column, in and past the core's bounding box.
func TestDenseCoreColumnMatchesCoreGrid(t *testing.T) {
	rng := rand.New(rand.NewSource(3012))
	for n := 0; n < 20; n++ {
		core := map[domain.Cell]bool{}
		for x := int32(3); x < 40; x++ {
			for z := int32(-5); z < 60; z++ {
				if rng.Intn(40) != 0 {
					core[domain.Cell{X: x, Z: z}] = true
				}
			}
		}
		g, d := buildGround{core: core}, newDenseCore(core)
		for x := int32(0); x < 44; x++ {
			for z := int32(-12); z < 66; z++ {
				if g.column(x, z) != d.column(x, z) {
					t.Fatalf("column(%d, %d): grid %v, dense %v", x, z, g.column(x, z), d.column(x, z))
				}
			}
		}
	}
	if (newDenseCore(nil)).column(0, 0) {
		t.Fatal("an empty core has a column")
	}
}

// TestRingPositionsMatchWindowScan proves ringPositions visits exactly the
// starts the whole padded window holds at gap ring: the scan placeNear used to
// run, kept here as the reference, for both gap metrics.
func TestRingPositionsMatchWindowScan(t *testing.T) {
	rng := rand.New(rand.NewSource(3012))
	for n := 0; n < 4000; n++ {
		ab := Rectangle{X: int32(rng.Intn(20)), Z: int32(rng.Intn(20)), Width: int32(1 + rng.Intn(9)), Height: int32(1 + rng.Intn(9))}
		sh := [2]int32{int32(1 + rng.Intn(9)), int32(1 + rng.Intn(9))}
		ring := 1 + rng.Intn(12)
		field := rng.Intn(2) == 0
		want := map[[2]int32]int{}
		for z := ab.Z - sh[1] - int32(ring); z <= ab.Z+ab.Height+int32(ring); z++ {
			for x := ab.X - sh[0] - int32(ring); x <= ab.X+ab.Width+int32(ring); x++ {
				r := Rectangle{X: x, Z: z, Width: sh[0], Height: sh[1]}
				g := gapDist(r, ab)
				if field {
					g = fieldGap(r, ab)
				}
				if g == ring {
					want[[2]int32{x, z}]++
				}
			}
		}
		got := map[[2]int32]int{}
		ringPositions(ab, sh, ring, field, func(x, z int32) { got[[2]int32{x, z}]++ })
		if len(got) != len(want) {
			t.Fatalf("anchor %+v shape %v ring %d field %v: %d starts, want %d", ab, sh, ring, field, len(got), len(want))
		}
		for p, c := range got {
			if want[p] != 1 || c != 1 {
				t.Fatalf("anchor %+v shape %v ring %d field %v: start %v visited %d times, window scan %d", ab, sh, ring, field, p, c, want[p])
			}
		}
	}
}
