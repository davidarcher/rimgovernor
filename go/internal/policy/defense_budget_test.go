package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestTurretBudgetStepsWithRaidPoints(t *testing.T) {
	cases := []struct {
		name   string
		points domain.Fact[float64]
		want   int
	}{
		{"unknown keeps the base budget", domain.Unknown[float64](), 2},
		{"zero", domain.Known(0.0), 2},
		{"negative", domain.Known(-50.0), 2},
		{"just under the mid step", domain.Known(299.9), 2},
		{"mid step", domain.Known(300.0), 4},
		{"just under the high step", domain.Known(799.9), 4},
		{"high step", domain.Known(800.0), 6},
		{"far above the cap", domain.Known(10000.0), 6},
		{"NaN keeps the base budget", domain.Known(math.NaN()), 2},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := TurretBudget(c.points); got != c.want {
				t.Fatalf("TurretBudget(%v) = %d, want %d", c.points, got, c.want)
			}
		})
	}
}

func TestTurretBudgetCapFitsTheCandidateProbe(t *testing.T) {
	if maxTurretCandidates < turretHardCap {
		t.Fatal("candidate probe cannot fill the hard cap", maxTurretCandidates, turretHardCap)
	}
}
