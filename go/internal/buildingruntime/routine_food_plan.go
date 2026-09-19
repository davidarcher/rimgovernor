package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// planFood retains one complete review per observed tick and invalidation
// generation, independently of additional definition/room reads by planners.
func (r *RoutineReviewer) planFood(p observation.ColonyProjection) domain.Fact[policy.FoodPlan] {
	s := &r.census
	s.mu.Lock()
	defer s.mu.Unlock()
	seasonal := r.seasonal(p.Facts)
	if _, known := s.foodPlan.Value(); known && s.foodGeneration == s.generation &&
		s.foodMin == seasonal.FoodMinDays && s.foodTarget == seasonal.FoodTargetDays && sameObservedIdentity(s.foodIdentity, p.Identity) {
		return s.foodPlan
	}
	plan := reviewFoodPlan(p, r.policy)
	if _, known := plan.Value(); known {
		s.foodIdentity, s.foodPlan, s.foodGeneration = p.Identity, plan, s.generation
		s.foodMin, s.foodTarget = seasonal.FoodMinDays, seasonal.FoodTargetDays
	}
	return plan
}

// reviewFoodPlan budgets the complete competing-consumer census. It is called
// by the routine review, before its reading is retained for method planners.
// A missing census never becomes an empty portfolio that certifies surplus.
func reviewFoodPlan(p observation.ColonyProjection, thresholds policy.RoutinePolicy) domain.Fact[policy.FoodPlan] {
	supply, sk := p.CombinedFoodSupply.Value()
	sources, ak := p.Acquisition.Value()
	if !sk || !ak {
		return domain.Unknown[policy.FoodPlan]()
	}
	workers, wk := p.Workers.Value()
	if pawns, pk := p.WorkPawns.Value(); pk {
		workers, wk = policy.RoutineWorkers(pawns).Value()
	}
	if !wk {
		return domain.Unknown[policy.FoodPlan]()
	}
	forecast, err := policy.ForecastFood(supply, nil)
	if err != nil {
		return domain.Unknown[policy.FoodPlan]()
	}
	channels := append(policy.ForageChannels(sources), policy.HuntChannels(sources)...)
	channels = append(channels, policy.StockIngredientChannels(supply)...)
	if fields, known := p.FoodFields.Value(); known {
		channels = append(channels, policy.CropChannels(fields)...)
	}
	// Capacity and stock protection do not create nutrition by themselves.
	// Zero-contribution Hold rows leave these supporting methods to their own
	// observed preconditions; their existing admission owns labor and resources.
	for _, support := range []struct {
		kind policy.FoodChannelKind
		id   string
	}{
		{policy.FoodCrop, "field-capacity"}, {policy.FoodCook, "cooking-capacity"}, {policy.FoodReserve, "stock-protection"},
	} {
		channels = append(channels, policy.FoodChannel{Kind: support.kind, ID: support.id,
			NutritionPerDay: domain.Known(0.0), WorkPerDay: domain.Known(0.0), LeadDays: domain.Known(0.0), Open: domain.Known(false),
			Terms: []policy.FoodPlanTerm{{Name: "supporting_method", Value: 1}}})
	}
	// Work capacity is a planning budget, not a promise of pawn work. Eight
	// hours per available worker leaves the rest of the day for sleep and needs.
	seasonal := thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions)
	plan, err := policy.PlanFood(policy.FoodPlanRequest{Demand: forecast,
		MinDays: seasonal.FoodMinDays, TargetDays: seasonal.FoodTargetDays,
		Channels: domain.Known(channels), Labor: domain.Known(float64(workers) * 20000)})
	if err != nil {
		return domain.Unknown[policy.FoodPlan]()
	}
	return domain.Known(plan)
}

func foodPlanSupport(p domain.Fact[policy.FoodPlan], kind policy.FoodChannelKind, id string) bool {
	plan, known := p.Value()
	if !known {
		return false
	}
	for _, entry := range plan.Portfolio {
		if entry.Channel.Kind == kind && entry.Channel.ID == id {
			return entry.Decision == policy.FoodPlanOpen || entry.Decision == policy.FoodPlanHold
		}
	}
	return false
}

// foodPlanAdditionalField counts only open zone actions; completed zones are
// already in the native field census. Infrastructure without a known crop yield
// keeps the existing work barrier rather than guessing its future production.
func foodPlanAdditionalField(p observation.ColonyProjection, plans []store.PlanState) bool {
	plan, known := p.Facts.FoodPlan.Value()
	if !known || plan.GapPerDay <= 0 {
		return false
	}
	gap := plan.GapPerDay
	for _, existing := range plans {
		for _, progress := range existing.Progress {
			if !domain.GoalWorkOpen([]domain.Progress{progress}) {
				continue
			}
			zone, ok := progress.Action().ZoneCreate()
			if !ok {
				if b, building := progress.Action().Building(); building && b.Definition() != "ButcherSpot" {
					return false
				}
				continue
			}
			found := false
			for _, d := range p.Definitions {
				if d.Name != zone.Crop() {
					continue
				}
				yield, yk := d.HarvestNutrition.Value()
				days, dk := d.GrowDays.Value()
				if !yk || !dk || yield < 0 || days <= 0 {
					return false
				}
				gap -= yield * float64(len(zone.Cells())) / days
				found = true
				break
			}
			if !found {
				return false
			}
		}
	}
	return gap > 0
}
