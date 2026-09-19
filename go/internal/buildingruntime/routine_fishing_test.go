package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestFoodPlanFishingIntegration(t *testing.T) {
	p := observation.ColonyProjection{Workers: domain.Known(2), Acquisition: domain.Known([]policy.AcquisitionSource{}),
		CombinedFoodSupply: domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "human", NutritionPerDay: domain.Known(3.0)}}})}
	thresholds := policy.DefaultRoutinePolicy()
	core, known := reviewFoodPlan(p, thresholds).Value()
	if !known {
		t.Fatal("Core food plan unavailable")
	}
	for _, row := range core.Portfolio {
		if row.Channel.Kind == policy.FoodFishing {
			t.Fatal("Core has a fishing row")
		}
	}
	p.FoodChannels = domain.Known(observation.FoodChannels{FishableWater: domain.Known(observation.FishableWater{FishingResearched: domain.Known(true), Regions: []observation.FishableRegion{{
		Root: domain.Cell{X: 5, Z: 8}, Population: domain.Known(300.0), MaxPopulation: domain.Known(300.0), NutritionPerFish: domain.Known(.25), FishPerBatch: domain.Known(6.0), WorkTicksPerBatch: domain.Known(7500.0), PawnFishWorkCapacity: domain.Known(8.0), Reachable: domain.Known(true), Frozen: domain.Known(false), Delivering: domain.Known(false),
	}}})})
	p.Facts.FoodPlan = reviewFoodPlan(p, thresholds)
	plan, known := p.Facts.FoodPlan.Value()
	if !known {
		t.Fatal("fishing plan unavailable")
	}
	found := false
	for _, row := range plan.Portfolio {
		if row.Channel.Kind == policy.FoodFishing {
			found = row.Channel.ID == "water-5-8" && row.Decision == policy.FoodPlanOpen && row.Channel.NutritionPerDay == domain.Known(1.875)
		}
	}
	if !found || len(fishingWork(p)) != 1 || fishingWork(p)[0].Work != policy.WorkFishing {
		t.Fatalf("fishing not selected: %+v", plan)
	}
}
