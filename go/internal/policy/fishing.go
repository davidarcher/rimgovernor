package policy

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FishingRegenerationPerDay is the ordinary game's fraction of a water body's
// maximum population restored per day. It is a fish count, not nutrition.
const FishingRegenerationPerDay = 0.025

// FishingRegion represents one shared population, regardless of how many zones
// or shallow cells offer access to it. Catch rates come from native definitions
// and the available fishers; they must not be inferred from zone area.
type FishingRegion struct {
	ID                                                string
	Population, MaxPopulation                         domain.Fact[float64]
	NutritionPerFish, FishPerBatch, WorkTicksPerBatch domain.Fact[float64]
	Reachable, Frozen, Open                           domain.Fact[bool]
}

type FishingRequest struct {
	Regions          []FishingRegion
	Researched       domain.Fact[bool]
	ResearchLeadDays domain.Fact[float64]
}

// FishingChannels estimates sustainable raw nutrition. Cooking conversion is a
// separate ledger contribution. Unknown facts never certify a delivery rate;
// malformed known facts return the same error as PlanFood. An empty region set
// (including Core without Odyssey) produces no fishing channel.
func FishingChannels(r FishingRequest) ([]FoodChannel, error) {
	if len(r.Regions) > 4096 {
		return nil, ErrFoodPlanFacts
	}
	if lead, known := r.ResearchLeadDays.Value(); known && !foodNumber(lead) {
		return nil, ErrFoodPlanFacts
	}
	seen := map[string]bool{}
	var channels []FoodChannel
	for _, region := range r.Regions {
		if !foodID(region.ID) || seen[region.ID] {
			return nil, ErrFoodPlanFacts
		}
		seen[region.ID] = true
		for _, f := range []domain.Fact[float64]{region.Population, region.MaxPopulation, region.NutritionPerFish, region.FishPerBatch, region.WorkTicksPerBatch} {
			if v, known := f.Value(); known && !foodNumber(v) {
				return nil, ErrFoodPlanFacts
			}
		}
		population, pk := region.Population.Value()
		maximum, mk := region.MaxPopulation.Value()
		nutrition, nk := region.NutritionPerFish.Value()
		batch, bk := region.FishPerBatch.Value()
		work, wk := region.WorkTicksPerBatch.Value()
		if pk && mk && population > maximum || nk && nutrition == 0 || bk && batch == 0 || wk && work == 0 {
			return nil, ErrFoodPlanFacts
		}
		channel := FoodChannel{Kind: FoodFishing, ID: region.ID, Open: region.Open}
		channel.Terms = []FoodPlanTerm{{Name: "fish_regeneration_fraction", Value: FishingRegenerationPerDay}}
		if researched, known := r.Researched.Value(); known {
			channel.LeadDays = r.ResearchLeadDays
			if researched {
				channel.LeadDays = domain.Known(0.0)
			} else {
				channel.Open = domain.Known(false)
				channel.Terms = append(channel.Terms, FoodPlanTerm{Name: "fishing_research_required", Value: 1})
			}
		}
		reachable, rk := region.Reachable.Value()
		frozen, fk := region.Frozen.Value()
		if rk && !reachable || fk && frozen || pk && population == 0 || mk && maximum == 0 {
			// Preserve an explicit explanation without presenting an unusable
			// region as an observed-open source of food.
			channel.Open = domain.Known(false)
			channel.NutritionPerDay, channel.WorkPerDay = domain.Known(0.0), domain.Known(0.0)
			if rk && !reachable {
				channel.Terms = append(channel.Terms, FoodPlanTerm{Name: "fishing_unreachable", Value: 1})
			}
			if fk && frozen {
				channel.Terms = append(channel.Terms, FoodPlanTerm{Name: "fishing_frozen", Value: 1})
			}
		} else if pk && mk && nk && bk && wk && rk && fk {
			// Low populations cannot supply more fish in the next day than
			// are present, even when the steady-state regeneration is higher.
			draw := math.Min(population, maximum*FishingRegenerationPerDay)
			dailyNutrition, dailyWork := draw*nutrition, draw/batch*work
			if !foodNumber(dailyNutrition) || !foodNumber(dailyWork) {
				return nil, ErrFoodPlanFacts
			}
			channel.NutritionPerDay, channel.WorkPerDay = domain.Known(dailyNutrition), domain.Known(dailyWork)
			channel.Terms = append(channel.Terms, FoodPlanTerm{Name: "fish_draw_per_day", Value: draw}, FoodPlanTerm{Name: "fish_per_batch", Value: batch})
		}
		channels = append(channels, channel)
	}
	return channels, nil
}

// FishingResearchRequest asks the existing research goal to open a selected
// source. A deferred, closed or unknown channel cannot change research intent.
func FishingResearchRequest(plan FoodPlan, researched domain.Fact[bool]) string {
	if known, ok := researched.Value(); !ok || known {
		return ""
	}
	for _, row := range plan.Portfolio {
		if row.Channel.Kind == FoodFishing && row.Decision == FoodPlanOpen && row.DeliveredPerDay > 0 {
			return "Fishing"
		}
	}
	return ""
}

// FishingRegionID is independent of zone identity: multiple zones in the same
// body must be deduplicated before they reach the nutrition ledger.
func FishingRegionID(root domain.Cell) string {
	return fmt.Sprintf("water-%d-%d", root.X, root.Z)
}
