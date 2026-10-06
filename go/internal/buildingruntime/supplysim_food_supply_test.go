package buildingruntime

// The food matrix through policy.PlanSupply (epic #2140, #2155): the same
// scenarios, projections and assertions as supplysim_food_test.go, with the
// channels reviewFoodPlan builds ranked by PlanSupply instead of PlanFood. The
// failing set must stay inside the baseline PlanFood recorded; a baselined
// failure PlanSupply fixes is logged as a flip.

import (
	"fmt"
	"maps"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// reviewFoodPlanBySupply is reviewFoodPlan with PlanSupply as the ranker: it
// takes the channels reviewFoodPlan assembled (portfolio and unknown rows) and
// the plan's own inputs, and reads the supply plan back as a FoodPlan.
func reviewFoodPlanBySupply(p observation.ColonyProjection, thresholds policy.RoundsPolicy) domain.Fact[policy.FoodPlan] {
	base, known := reviewFoodPlan(p, thresholds).Value()
	workers, wk := p.Workers.Value()
	if pawns, pk := p.WorkPawns.Value(); pk {
		workers, wk = policy.RoundsWorkers(pawns).Value()
	}
	if !known || !wk {
		return domain.Unknown[policy.FoodPlan]()
	}
	seasonal := thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions)
	demand, err := policy.NutritionDemand(policy.NutritionDemandInput{Forecast: base.Forecast, MinDays: seasonal.FoodMinDays, TargetDays: seasonal.FoodTargetDays, EmergencyDays: seasonal.FootholdFoodDays})
	if err != nil {
		return domain.Unknown[policy.FoodPlan]()
	}
	var candidates []policy.SupplyCandidate
	for _, e := range append(append([]policy.FoodPlanEntry(nil), base.Portfolio...), base.Unknown...) {
		candidates = append(candidates, policy.SupplyCandidateOfFood(e.Channel))
	}
	plan, err := policy.PlanSupply(policy.SupplyPlanRequest{Demands: domain.Known([]policy.SupplyDemand{demand}), Candidates: domain.Known(candidates), Labor: domain.Known(float64(workers) * 20000)})
	if err != nil {
		return domain.Unknown[policy.FoodPlan]()
	}
	out := policy.FoodPlan{Forecast: base.Forecast, DemandPerDay: base.DemandPerDay, GapPerDay: plan.Gap(policy.NutritionKey), DeliveredPerDay: plan.Delivered(policy.NutritionKey)}
	for _, e := range plan.Portfolio {
		channel, _ := policy.FoodChannelOfSupply(e.Candidate)
		out.Portfolio = append(out.Portfolio, policy.FoodPlanEntry{Channel: channel, Decision: policy.FoodPlanDecision(e.Decision), Reason: e.Reason})
	}
	return domain.Known(out)
}

func runFoodSupplyMatrix(t *testing.T, horizon int) {
	suffix := fmt.Sprintf("@%d", horizon)
	scenarios := foodScenarios(t)
	results := map[string]foodResult{}
	for _, sc := range scenarios {
		results[sc.name] = runFoodWith(sc, horizon, true)
	}
	base := loadFoodBaseline(t)
	current := map[string][]string{}
	for _, sc := range scenarios {
		if fails := foodFailures(sc, results); len(fails) > 0 {
			current[sc.name+suffix] = fails
		}
	}
	for _, k := range slices.Sorted(maps.Keys(current)) {
		for _, f := range current[k] {
			if !slices.Contains(base[k], f) {
				t.Errorf("PlanSupply fails %s %q, which is not in the PlanFood baseline", k, f)
			}
		}
	}
	for _, k := range slices.Sorted(maps.Keys(base)) {
		if !strings.HasSuffix(k, suffix) {
			continue
		}
		for _, f := range base[k] {
			if !slices.Contains(current[k], f) {
				t.Logf("flip: %s now passes %q under PlanSupply", k, f)
			}
		}
	}
}

func TestFoodMatrixPlanSupply(t *testing.T) { runFoodSupplyMatrix(t, foodShortHorizon) }

func TestFoodMatrixPlanSupplyLongHorizon(t *testing.T) {
	slowtest.Skip(t, "90 simulated days per scenario")
	runFoodSupplyMatrix(t, foodLongHorizon)
}
