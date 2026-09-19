package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A standing stove alone does not satisfy a requested change in meal tier.
func (r *RoutineReviewer) reviewMeals(p *observation.ColonyProjection) {
	seasonal := r.seasonal(p.Facts)
	request := p.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays)
	review, err := policy.ReviewMealTier(request, p.ProductionBenches)
	if err != nil {
		return
	}
	clockSchedulerLog("meals: %s", review.Explain())
	if review.Tier == policy.MealPaste {
		channels, known := p.FoodChannels.Value()
		if !known {
			return
		}
		ready := false
		for _, dispenser := range channels.PasteDispenser {
			powered, pk := dispenser.Powered.Value()
			nutrition, nk := dispenser.HopperNutrition.Value()
			ready = ready || pk && powered && nk && nutrition > 0
		}
		p.Facts.Cooking = domain.Known(ready)
		return
	}
	if len(review.Recipes) == 0 {
		return
	}
	benches, _ := p.ProductionBenches.Value()
	ready := false
	for _, choice := range review.Recipes {
		for _, bench := range benches {
			if bench.ID == choice.Bench {
				for _, bill := range bench.Bills {
					active, known := bill.Active.Value()
					ready = ready || known && active && bill.Recipe == choice.Recipe
				}
			}
		}
	}
	p.Facts.Cooking = domain.Known(ready)
}
