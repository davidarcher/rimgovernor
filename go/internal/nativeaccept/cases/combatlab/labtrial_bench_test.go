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
