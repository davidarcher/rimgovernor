package food

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ChannelExpectation selects portfolio rows by kind and decision. ID optionally
// narrows the assertion to one source. Minimums apply to each matching row,
// not a sum that could hide an empty source. Unknown rows never satisfy it.
type ChannelExpectation struct {
	Kind               policy.FoodChannelKind
	Decision           policy.FoodPlanDecision
	ID                 string
	MinNutritionPerDay float64
	MinDeliveredPerDay float64
}

// CheckChannel returns matching rows for the acceptance report, requiring
// finite nutrition and the planner's explained rate, labor, lead, risk and
// cover terms. A Hold may deliver zero when deferring a closed channel.
func CheckChannel(plan policy.FoodPlan, want ChannelExpectation) ([]policy.FoodPlanEntry, error) {
	if !finiteNonnegative(want.MinNutritionPerDay) || !finiteNonnegative(want.MinDeliveredPerDay) {
		return nil, fmt.Errorf("invalid channel expectation: %+v", want)
	}
	var rows []policy.FoodPlanEntry
	for _, row := range plan.Portfolio {
		if row.Channel.Kind != want.Kind || row.Decision != want.Decision || want.ID != "" && row.Channel.ID != want.ID {
			continue
		}
		nutrition, known := row.Channel.NutritionPerDay.Value()
		if !known || !finiteNonnegative(nutrition) || nutrition < want.MinNutritionPerDay ||
			!finiteNonnegative(row.DeliveredPerDay) || row.DeliveredPerDay < want.MinDeliveredPerDay {
			return nil, fmt.Errorf("%s/%s has insufficient or unknown nutrition: %+v", want.Kind, row.Channel.ID, row)
		}
		terms := make(map[string]float64, len(row.Terms))
		for _, term := range row.Terms {
			if math.IsNaN(term.Value) || math.IsInf(term.Value, 0) {
				return nil, fmt.Errorf("%s/%s has non-finite %s term", want.Kind, row.Channel.ID, term.Name)
			}
			if _, exists := terms[term.Name]; exists {
				return nil, fmt.Errorf("%s/%s repeats %s term", want.Kind, row.Channel.ID, term.Name)
			}
			terms[term.Name] = term.Value
		}
		for _, key := range []string{"nutrition_per_day", "work_per_day", "lead_days", "risk_discount", "target_cover"} {
			value, ok := terms[key]
			if !ok || !finiteNonnegative(value) {
				return nil, fmt.Errorf("%s/%s lacks valid explain term %s", want.Kind, row.Channel.ID, key)
			}
		}
		if terms["nutrition_per_day"] < want.MinNutritionPerDay || row.Reason == "" {
			return nil, fmt.Errorf("%s/%s lacks explained nutrition or decision reason", want.Kind, row.Channel.ID)
		}
		rows = append(rows, row)
	}
	if len(rows) == 0 {
		return nil, fmt.Errorf("no %s/%s %s portfolio rows; %s", want.Kind, want.ID, want.Decision, plan.Explain())
	}
	return rows, nil
}

// CheckBaselinePlan is the ledger assertion for the tribal8 pre-harvest case:
// both bridge channels must be opened with positive admitted nutrition/day.
// The caller must establish the native pre-harvest state and obtain the live
// controller plan; constructing a plan here would not prove goal integration.
func CheckBaselinePlan(plan policy.FoodPlan) error {
	for _, kind := range []policy.FoodChannelKind{policy.FoodForage, policy.FoodHunt} {
		if _, err := CheckChannel(plan, ChannelExpectation{Kind: kind, Decision: policy.FoodPlanOpen,
			MinNutritionPerDay: 1e-9, MinDeliveredPerDay: 1e-9}); err != nil {
			return err
		}
	}
	return nil
}

func finiteNonnegative(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) && v >= 0 }
