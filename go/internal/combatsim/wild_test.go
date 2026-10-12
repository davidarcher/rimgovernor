package combatsim

import (
	"math"
	"testing"
)

// TestMissDistanceFollowsTheShootTuningSurface pins the columns of
// ShootTuning.MissDistanceFromAimOnChanceCurves: every column starts at 1 cell
// and ends at 10, 8, 6, 4, 2 and 1 cells; between columns the two are blended.
func TestMissDistanceFollowsTheShootTuningSurface(t *testing.T) {
	for _, c := range []struct{ aim, u, want float64 }{
		{0.01, 0, 1}, {0.01, 1, 10}, {0.02, 0.5, 5.5},
		{0.04, 1, 8}, {0.07, 1, 6}, {0.11, 1, 4}, {0.22, 1, 2},
		{0.5, 1, 1 + (2-1)*(1-(0.5-0.22)/(1-0.22))}, {1, 1, 1}, {2, 1, 1},
		{0.03, 1, 9}, // halfway between the 0.02 and 0.04 columns
	} {
		if got := missDistance(c.aim, c.u); math.Abs(got-c.want) > 1e-9 {
			t.Errorf("missDistance(%v, %v) = %v, want %v", c.aim, c.u, got, c.want)
		}
	}
}
