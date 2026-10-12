package combatlab

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim/simimport"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// labRangedFight is the imported lab-ranged fixture under the calibration env:
// the real policy, the enemy AI and Autonomy, as the calibration gate runs it.
func labRangedFight(tb testing.TB) combatsim.Fight {
	tb.Helper()
	cx, cz := labCenter()
	f, err := Build("lab-ranged", cx, cz)
	if err != nil {
		tb.Fatal(err)
	}
	spec, err := SimSpec(f, CalibrationEnv())
	if err != nil {
		tb.Fatal(err)
	}
	res, err := simimport.Import(recordedcatalog.Catalog(tb), spec)
	if err != nil {
		tb.Fatal(err)
	}
	return res.Fight
}

// BenchmarkLabRangedTrial is the cost of one complete lab-ranged trial through
// RunFight, which the constants search (#2848) runs thousands of times. Run it
// with -cpu 1,8,16 to see the parallel trials/s.
//
// Measured on a 16-core Ryzen 9 9950X (#2989, after the E9 to E11 merges, a
// shared box so single runs vary by about 30%): 1 worker 170-260 trials/s
// (was 425 before E9), 4 workers ~815, 8 workers ~1,550, 16 workers ~2,400
// (best runs ~3,100). The research note's 1,000 trials/s target therefore holds
// from 5 to 6 workers, not on one. A trial is about 4 ms; the per-part injury
// model is under 1% of it (the cost is the policy's per-stop decision, the
// raiders' AI and the garbage they make: rankThreats alone was a third of the
// allocated bytes before it stopped copying pawn rows into a map).
func BenchmarkLabRangedTrial(b *testing.B) {
	fight := labRangedFight(b)
	tuning := policy.DefaultCombatTuning()
	b.ReportAllocs()
	b.RunParallel(func(pb *testing.PB) {
		seed := uint64(1)
		for pb.Next() {
			if _, err := combatsim.RunFight(fight, seed, tuning); err != nil {
				b.Error(err)
				return
			}
			seed++
		}
	})
	b.ReportMetric(float64(b.N)/b.Elapsed().Seconds(), "trials/s")
}
