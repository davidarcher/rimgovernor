package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestResidualSupplyLabor(t *testing.T) {
	channel := foodPlanChannel("food", 10, 3000, 0, false)
	entry := func(state domain.Fact[CandidateState], decision FoodPlanDecision) FoodPlanEntry {
		c := channel
		c.State = state
		return FoodPlanEntry{Channel: c, Decision: decision}
	}
	designated := entry(domain.Known(CandidateDesignated), FoodPlanHold)
	opened := entry(domain.Known(CandidateClosed), FoodPlanOpen)
	delivering := entry(domain.Known(CandidateDelivering), FoodPlanHold)
	closed := entry(domain.Known(CandidateClosed), FoodPlanHold)
	closed.Channel.LaborPerDay = domain.Unknown[float64]()
	missingCost := designated
	missingCost.Channel.LaborPerDay = domain.Unknown[float64]()
	multi := opened
	multi.Channel.Yields = append(multi.Channel.Yields, CandidateYield{Good: ResourceKey{Def: "Leather"}, PerDay: domain.Known(2.0)})
	for _, tc := range []struct {
		name    string
		workers domain.Fact[int]
		plan    domain.Fact[FoodPlan]
		want    float64
		known   bool
	}{
		{"unknown workers", domain.Unknown[int](), domain.Known(FoodPlan{}), 0, false},
		{"missing plan", domain.Known(1), domain.Unknown[FoodPlan](), 0, false},
		{"empty plan", domain.Known(1), domain.Known(FoodPlan{}), 20000, true},
		{"zero workers", domain.Known(0), domain.Known(FoodPlan{}), 0, true},
		{"new commitment", domain.Known(1), domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{opened}}), 17000, true},
		{"designated", domain.Known(1), domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{designated}}), 17000, true},
		{"delivering", domain.Known(1), domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{delivering}}), 17000, true},
		{"unknown section commitment", domain.Known(1), domain.Known(FoodPlan{Unknown: []FoodPlanEntry{designated}}), 17000, true},
		{"missing cost", domain.Known(1), domain.Known(FoodPlan{Unknown: []FoodPlanEntry{missingCost}}), 0, false},
		{"missing state", domain.Known(1), domain.Known(FoodPlan{Unknown: []FoodPlanEntry{entry(domain.Unknown[CandidateState](), FoodPlanHold)}}), 0, false},
		{"closed unselected missing cost", domain.Known(1), domain.Known(FoodPlan{Unknown: []FoodPlanEntry{closed}}), 20000, true},
		{"plan closes", domain.Known(1), domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{entry(domain.Known(CandidateDelivering), FoodPlanClose)}}), 20000, true},
		{"identity and yields once", domain.Known(1), domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{multi}, Unknown: []FoodPlanEntry{opened}}), 17000, true},
		{"exhausted", domain.Known(0), domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{opened}}), 0, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := ResidualSupplyLabor(tc.workers, tc.plan)
			value, known := got.Value()
			if err != nil || known != tc.known || known && value != tc.want {
				t.Fatalf("got %v/%v, %v; want %v/%v", value, known, err, tc.want, tc.known)
			}
		})
	}
	for _, work := range []float64{-1, math.NaN(), math.Inf(1)} {
		bad := designated
		bad.Channel.LaborPerDay = domain.Known(work)
		if _, err := ResidualSupplyLabor(domain.Known(1), domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{bad}})); err == nil {
			t.Fatal("accepted invalid cost", work)
		}
	}
}
