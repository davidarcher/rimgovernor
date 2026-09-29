package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWealthBudgetSignAndScale(t *testing.T) {
	for _, c := range []struct {
		name                     string
		points, capacity, wealth float64
		want                     float64
	}{
		{"balanced", 1000, 1000, 100000, 0},
		{"capacity double affords double wealth", 1000, 2000, 100000, 100000},
		{"capacity half sheds half", 1000, 500, 100000, -50000},
		{"no defense sheds everything", 1000, 0, 100000, -100000},
		{"raid points floored at 35", 0, 70, 10000, 10000},
		{"no wealth no headroom", 500, 0, 0, 0},
	} {
		got, ok := WealthBudget(domain.Known(c.points), domain.Known(c.capacity), domain.Known(WealthFacts{Total: c.wealth})).Value()
		if !ok || math.Abs(got-c.want) > 1e-6 {
			t.Errorf("%s: %v %v want %v", c.name, got, ok, c.want)
		}
	}
	for name, b := range map[string]domain.Fact[float64]{
		"unknown points":   WealthBudget(domain.Unknown[float64](), domain.Known(1.0), domain.Known(WealthFacts{Total: 1})),
		"unknown capacity": WealthBudget(domain.Known(1.0), domain.Unknown[float64](), domain.Known(WealthFacts{Total: 1})),
		"unknown wealth":   WealthBudget(domain.Known(1.0), domain.Known(1.0), domain.Unknown[WealthFacts]()),
		"NaN capacity":     WealthBudget(domain.Known(1.0), domain.Known(math.NaN()), domain.Known(WealthFacts{Total: 1})),
	} {
		if _, ok := b.Value(); ok {
			t.Errorf("%s known", name)
		}
	}
}
