package buildingruntime

import (
	"context"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (r *RoutineAnimalFeedPlanner) planHay(call, epoch context.Context, state ControlState, goal store.GoalState, read observation.RoutineReading) (RoutineResourceResult, bool, error) {
	p := read.Projection
	need, known := policy.HayNutritionNeed(p.Facts.PenGrazing, policy.HarvestGapDays(p.Facts.Calendar, p.Facts.DisasterConditions)).Value()
	if !known || need <= 0 {
		return RoutineResourceResult{}, false, nil
	}
	var crop policy.CropChoice
	for _, d := range p.Definitions {
		if d.Name == "Plant_Haygrass" {
			crop = policy.CropChoice{Name: d.Name, Available: d.Available, Edible: domain.Known(false), GrowDays: d.GrowDays, HarvestNutrition: d.HarvestNutrition, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, SowTags: d.SowTags, MinGlow: d.GrowMinGlow}
		}
	}
	yield, yk := crop.HarvestNutrition.Value()
	if !yk {
		return RoutineResourceResult{}, false, nil
	}
	for _, zone := range p.Farms {
		if zone.Crop == crop.Name {
			cells, ck := zone.UsableCells.Value()
			if !ck {
				return RoutineResourceResult{}, false, nil
			}
			need = math.Max(0, need-float64(cells)*yield)
		}
	}
	if need <= 0 {
		return RoutineResourceResult{}, false, nil
	}
	held, err := r.reviewer.player.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	var protected []domain.Cell
	for _, reservation := range held {
		protected = append(protected, reservation.Footprint...)
	}
	plans, err := r.reviewer.player.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	claims, err := r.reviewer.player.journal.ConstructionClaims(call, state.Snapshot, p.Identity.Tick)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	claimed, _ := claims.Value()
	protected = append(protected, shellInteriors(plans, claimed)...)
	plan, ok := policy.PlanHayField(domain.Known(need), crop, p.CropClimate)
	if !ok {
		return RoutineResourceResult{}, false, nil
	}
	fields := &RoutineFieldPlanner{reviewer: r.reviewer, native: r.native}
	result, tried, err := fields.enactBlock(call, epoch, state, goal, p, read, 0, policy.SiteTypeCandidate{Kind: policy.SiteOutdoor, Crop: plan.Crop, Needed: plan.Needed}, []policy.FieldBlockOption{{Crop: plan.Crop, Needed: plan.Needed}}, layoutAnchor(p, policy.DistrictFields), protected)
	return RoutineResourceResult{Reason: result.Reason, Plan: result.Plan}, tried, err
}
