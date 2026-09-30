package buildingruntime

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// hayShortfall is the haygrass cells the pens still need after standing hay
// zones (#1308): pure demand; RoutineFieldPlanner places the block.
func hayShortfall(p observation.ColonyProjection) (policy.FieldBlockOption, bool) {
	need, known := policy.HayNutritionNeed(p.Facts.PenGrazing, policy.HarvestGapDays(p.Facts.Calendar, p.Facts.DisasterConditions)).Value()
	if !known || need <= 0 {
		return policy.FieldBlockOption{}, false
	}
	var crop policy.CropChoice
	for _, d := range p.Definitions {
		if d.Name == "Plant_Haygrass" {
			crop = policy.CropChoice{Name: d.Name, Available: d.Available, Edible: domain.Known(false), GrowDays: d.GrowDays, HarvestNutrition: d.HarvestNutrition, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, SowTags: d.SowTags, MinGlow: d.GrowMinGlow}
		}
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
