package buildingruntime

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// hayCrop is the catalog's crop that harvests hay, as a non-edible choice.
func hayCrop(p observation.ColonyProjection) (policy.CropChoice, bool) {
	for _, d := range p.Definitions {
		crop := withHarvestFacts(policy.CropChoice{Name: d.Name, Available: d.Available, Edible: domain.Known(false), GrowDays: d.GrowDays, HarvestNutrition: d.HarvestNutrition, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, SowTags: d.SowTags, MinGlow: d.GrowMinGlow}, d)
		if policy.IsHayCrop(crop) {
			return crop, true
		}
	}
	return policy.CropChoice{}, false
}

// hayTarget is the hay crop and the cells the pens' whole need takes, before
// standing hay zones (#1309); false when unknown or out of season.
func hayTarget(p observation.ColonyProjection) (string, int, bool) {
	need, known := policy.HayNutritionNeed(p.Facts.PenGrazing, policy.HarvestGapDays(p.Facts.Calendar, p.Facts.DisasterConditions)).Value()
	if !known || need <= 0 {
		return "", 0, false
	}
	crop, ok := hayCrop(p)
	if !ok {
		return "", 0, false
	}
	plan, ok := policy.PlanHayField(domain.Known(need), crop, p.CropClimate)
	return crop.Name, plan.Needed, ok && plan.Needed > 0
}

// hayShortfall is the hay cells the pens still need after standing hay
// zones (#1308): pure demand; RoundsFieldPlanner places the block.
func hayShortfall(p observation.ColonyProjection) (policy.FieldBlockOption, bool) {
	need, known := policy.HayNutritionNeed(p.Facts.PenGrazing, policy.HarvestGapDays(p.Facts.Calendar, p.Facts.DisasterConditions)).Value()
	if !known || need <= 0 {
		return policy.FieldBlockOption{}, false
	}
	crop, ok := hayCrop(p)
	if !ok {
		return policy.FieldBlockOption{}, false
	}
	yield, yk := crop.HarvestNutrition.Value()
	if !yk {
		return policy.FieldBlockOption{}, false
	}
	for _, zone := range p.Farms {
		if zone.Crop == crop.Name {
			cells, ck := zone.UsableCells.Value()
			if !ck {
				return policy.FieldBlockOption{}, false
			}
			need = math.Max(0, need-float64(cells)*yield)
		}
	}
	if need <= 0 {
		return policy.FieldBlockOption{}, false
	}
	plan, ok := policy.PlanHayField(domain.Known(need), crop, p.CropClimate)
	if !ok || plan.Needed <= 0 {
		return policy.FieldBlockOption{}, false
	}
	return policy.FieldBlockOption{Crop: plan.Crop, Needed: plan.Needed}, true
}
