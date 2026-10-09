package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestTurretBudgetScalesWithRaidPointsWithoutCeiling(t *testing.T) {
	cases := []struct {
		name   string
		points domain.Fact[float64]
		want   int
	}{
		{"unknown keeps the base budget", domain.Unknown[float64](), 2},
		{"zero", domain.Known(0.0), 2},
		{"negative", domain.Known(-50.0), 2},
		{"just under the first step", domain.Known(299.9), 2},
		{"first step", domain.Known(300.0), 4},
		{"second step", domain.Known(600.0), 6},
		{"third step", domain.Known(900.0), 8},
		{"far above the old cap", domain.Known(10000.0), 68},
		{"NaN keeps the base budget", domain.Known(math.NaN()), 2},
		{"infinite keeps the base budget", domain.Known(math.Inf(1)), 2},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := TurretBudget(c.points); got != c.want {
				t.Fatalf("TurretBudget(%v) = %d, want %d", c.points, got, c.want)
			}
		})
	}
}

func TestIEDBudgetScalesWithRaidPoints(t *testing.T) {
	for points, want := range map[float64]int{0: 2, 299: 2, 300: 3, 1500: 7} {
		if got := IEDBudget(domain.Known(points)); got != want {
			t.Fatalf("IEDBudget(%v) = %d, want %d", points, got, want)
		}
	}
	if got := IEDBudget(domain.Unknown[float64]()); got != 2 {
		t.Fatal(got)
	}
}
