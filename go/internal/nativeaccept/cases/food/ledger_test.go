package food

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func ledgerRow(kind policy.FoodChannelKind, decision policy.FoodPlanDecision) policy.FoodPlanEntry {
	return policy.FoodPlanEntry{Channel: policy.FoodChannel{Kind: kind, ID: "source", NutritionPerDay: domain.Known(2.0)},
		Decision: decision, Reason: "close nutrition gap", DeliveredPerDay: 2,
		Terms: []policy.FoodPlanTerm{{Name: "nutrition_per_day", Value: 2}, {Name: "work_per_day", Value: 2500},
			{Name: "lead_days", Value: 0}, {Name: "risk_discount", Value: 0}, {Name: "target_cover", Value: 1}}}
}

func TestBaselineLedgerRequiresBothExplainedOpenChannels(t *testing.T) {
	for _, name := range []string{"valid", "hunt-missing", "hunt-unknown", "hold", "zero", "unknown-rate", "no-delivery", "term-missing", "nan-term", "duplicate-term", "no-reason"} {
		t.Run(name, func(t *testing.T) {
			plan := policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{ledgerRow(policy.FoodForage, policy.FoodPlanOpen), ledgerRow(policy.FoodHunt, policy.FoodPlanOpen)}}
			row := &plan.Portfolio[1]
			switch name {
			case "hunt-missing":
				plan.Portfolio = plan.Portfolio[:1]
			case "hunt-unknown":
				plan.Unknown = plan.Portfolio[1:]
				plan.Portfolio = plan.Portfolio[:1]
			case "hold":
				row.Decision = policy.FoodPlanHold
			case "zero":
				row.Channel.NutritionPerDay = domain.Known(0.0)
			case "unknown-rate":
				row.Channel.NutritionPerDay = domain.Unknown[float64]()
			case "no-delivery":
				row.DeliveredPerDay = 0
			case "term-missing":
				row.Terms = row.Terms[1:]
			case "nan-term":
				row.Terms[0].Value = math.NaN()
			case "duplicate-term":
				row.Terms = append(row.Terms, row.Terms[0])
			case "no-reason":
				row.Reason = ""
			}
			if err := CheckBaselinePlan(plan); (err == nil) != (name == "valid") {
				t.Fatalf("CheckBaselinePlan: %v", err)
			}
		})
	}
}

func TestChannelDecisionAndSourceSelection(t *testing.T) {
	for _, decision := range []policy.FoodPlanDecision{policy.FoodPlanOpen, policy.FoodPlanHold, policy.FoodPlanClose} {
		row := ledgerRow(policy.FoodCrop, decision)
		row.DeliveredPerDay = 0
		plan := policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{row}}
		want := ChannelExpectation{Kind: policy.FoodCrop, Decision: decision, ID: "source"}
		if rows, err := CheckChannel(plan, want); err != nil || len(rows) != 1 {
			t.Fatalf("%s: %v", decision, err)
		}
		want.ID = "other"
		if _, err := CheckChannel(plan, want); err == nil {
			t.Fatal("wrong source passed")
		}
	}
}

func TestBaselineAssertionAcceptsPlannerExplainContract(t *testing.T) {
	sources := []policy.AcquisitionSource{
		{ID: "berries", Food: true, NutritionYield: 1},
		{ID: "deer", Food: true, Hunt: true, NutritionYield: 4},
	}
	rows := append(policy.ForageChannels(sources), policy.HuntChannels(sources)...)
	plan, err := policy.PlanFood(policy.FoodPlanRequest{
		Demand: policy.FoodForecast{RunwayDays: domain.Known(1.0),
			Consumers: []policy.ConsumerFoodForecast{{ID: "pawn", NutritionPerDay: 10}}},
		MinDays: 2, TargetDays: 4, Channels: domain.Known(rows), Labor: domain.Known(100000.0),
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := CheckBaselinePlan(plan); err != nil {
		t.Fatal(err)
	}
}
