package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResidualSupplyLabor is the daily budget after selected or retained food
// commitments. Unknown commitment state or cost leaves the residual unknown.
// A known empty plan reserves nothing; each kind/ID is charged once.
func ResidualSupplyLabor(workers domain.Fact[int], food domain.Fact[FoodPlan]) (domain.Fact[float64], error) {
	count, workersKnown := workers.Value()
	if workersKnown && count < 0 {
		return domain.Unknown[float64](), ErrSupplyPlanFacts
	}
	plan, planKnown := food.Value()
	known := workersKnown && planKnown
	budget := float64(count) * 20000
	charged := map[[2]string]bool{}
	for _, rows := range [][]FoodPlanEntry{plan.Portfolio, plan.Unknown} {
		for _, e := range rows {
			if e.Decision == FoodPlanClose {
				continue
			}
			state, stateKnown := e.Channel.State.Value()
			committed := e.Selected() || stateKnown && state != CandidateClosed
			if !committed {
				if !stateKnown {
					known = false
				}
				continue
			}
			work, workKnown := e.Channel.LaborPerDay.Value()
			if workKnown && !foodNumber(work) {
				return domain.Unknown[float64](), ErrSupplyPlanFacts
			}
			if !workKnown {
				known = false
				continue
			}
			id := [2]string{string(e.Channel.Kind), e.Channel.ID}
			if !charged[id] {
				budget -= work
				charged[id] = true
			}
		}
	}
	if !known {
		return domain.Unknown[float64](), nil
	}
	if math.IsInf(budget, 0) || math.IsNaN(budget) {
		return domain.Unknown[float64](), ErrSupplyPlanFacts
	}
	return domain.Known(math.Max(0, budget)), nil
}
