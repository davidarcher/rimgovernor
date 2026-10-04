package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// socialShortfalls is each social crop's missing cells under the fixed
// nine-cell ceiling (#1226), counting existing player fields without
// changing them: pure demand; RoundsFieldPlanner places the blocks
// (#1308). known is false while a social field's size is unobserved.
func socialShortfalls(projection observation.ColonyProjection) ([]policy.FieldBlockOption, bool) {
	var out []policy.FieldBlockOption
	for _, name := range []string{"Plant_Hops", "Plant_Smokeleaf"} {
		cells := 0
		for _, farm := range projection.Farms {
			if farm.Crop == name {
				n, known := farm.UsableCells.Value()
				if !known {
					return nil, false
				}
				cells += int(n)
			}
		}
		for _, d := range projection.Definitions {
			if d.Name == name {
				crop := policy.CropChoice{Name: name, Available: d.Available, Edible: d.Edible, GrowDays: d.GrowDays, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, SowTags: d.SowTags, MinGlow: d.GrowMinGlow, RequiresPollution: d.RequiresPollution, RequiresCleanSoil: d.RequiresCleanSoil}
				if needed := policy.PlanSocialCrop(crop, projection.CropClimate, cells); needed > 0 {
					out = append(out, policy.FieldBlockOption{Crop: crop, Needed: needed})
				}
			}
		}
	}
	return out, true
}
