package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Social fields follow food recovery and use a fixed nine-cell ceiling per
// crop, counting existing player fields without changing their configuration.
func (r *RoutineFieldPlanner) socialFields(call, epoch context.Context, state ControlState, review store.RoutineReview) (RoutineFieldResult, error) {
	if !review.BrewingFinished {
		return RoutineFieldResult{Reason: BuildingMethodNoDeficit}, nil
	}
	p := r.reviewer.player
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainResource {
			var err error
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			if err != nil {
				return RoutineFieldResult{}, err
			}
		}
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineFieldResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineFieldResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineFieldResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil || !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineFieldResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, "Plant_Hops", "Plant_Smokeleaf")
	if err != nil {
		return RoutineFieldResult{}, err
	}
	projection := read.Projection
	if !policy.BrewingFinished(projection.Facts.Research) {
		return RoutineFieldResult{Reason: BuildingMethodUnknown}, nil
	}
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineFieldResult{Reason: BuildingMethodUnknown}, nil
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	for _, name := range []string{"Plant_Hops", "Plant_Smokeleaf"} {
		cells := 0
		for _, farm := range projection.Farms {
			if farm.Crop == name {
				n, known := farm.UsableCells.Value()
				if !known {
					return RoutineFieldResult{Reason: BuildingMethodUnknown}, nil
				}
				cells += int(n)
			}
		}
		for _, d := range projection.Definitions {
			if d.Name == name {
				crop := policy.CropChoice{Name: name, Available: d.Available, Edible: d.Edible, GrowDays: d.GrowDays, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, SowTags: d.SowTags, MinGlow: d.GrowMinGlow, RequiresPollution: d.RequiresPollution, RequiresCleanSoil: d.RequiresCleanSoil}
				socialSite := policy.FarmSiteRequest{Bounds: projection.Bounds, Anchor: layoutAnchor(projection, policy.DistrictFields), Cells: projection.Cells, Protected: layoutProtected(projection, protected), Weights: layoutFarmWeights(projection)}
				socialSite.Grid, _ = layoutAlignment(projection)
				sites := policy.PlanSocialCrop(crop, projection.CropClimate, cells, socialSite)
				if sites.Cells == 0 {
					continue
				}
				result, tried, err := r.enact(call, epoch, state, goal, projection, read, 0, policy.SiteTypeCandidate{Kind: policy.SiteOutdoor, Crop: crop, Sites: sites, Cells: sites.Cells}, token)
				if tried || err != nil {
					return result, err
				}
			}
		}
	}

	return RoutineFieldResult{Reason: BuildingMethodNoDeficit}, nil
}
