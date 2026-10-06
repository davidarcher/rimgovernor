package buildingruntime

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// socialCrops are the catalog's crops that harvest a social-drug ingredient,
// ordered by name.
func socialCrops(projection observation.ColonyProjection) []policy.CropChoice {
	var out []policy.CropChoice
	for _, d := range projection.Definitions {
		crop := withHarvestFacts(policy.CropChoice{Name: d.Name, Available: d.Available, Edible: d.Edible, GrowDays: d.GrowDays, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, SowTags: d.SowTags, MinGlow: d.GrowMinGlow, RequiresPollution: d.RequiresPollution, RequiresCleanSoil: d.RequiresCleanSoil}, d)
		if policy.IsSocialCrop(crop) {
			out = append(out, crop)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// socialShortfalls is each social crop's missing cells under the fixed
// nine-cell ceiling (#1226), counting existing player fields without
// changing them: pure demand; RoundsFieldPlanner places the blocks
// (#1308). known is false while a social field's size is unobserved.
func socialShortfalls(projection observation.ColonyProjection) ([]policy.FieldBlockOption, bool) {
	var out []policy.FieldBlockOption
	for _, crop := range socialCrops(projection) {
		cells := 0
		for _, farm := range projection.Farms {
			if farm.Crop == crop.Name {
				n, known := farm.UsableCells.Value()
				if !known {
					return nil, false
				}
				cells += int(n)
			}
		}
		if needed := policy.PlanSocialCrop(crop, projection.CropClimate, cells); needed > 0 {
			out = append(out, policy.FieldBlockOption{Crop: crop, Needed: needed})
		}
	}
	return out, true
}
