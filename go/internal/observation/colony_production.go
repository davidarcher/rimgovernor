package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyFieldCrops(v *o.ColonyFactsSnapshot, definitions []PlanningDefinition) domain.Fact[[]policy.FieldCrop] {
	if hasIssue(v.Issues, "farms") {
		return domain.Unknown[[]policy.FieldCrop]()
	}
	rows := make([]policy.FieldCrop, 0, len(v.Farms))
	for _, farm := range v.Farms {
		row := policy.FieldCrop{Edible: optional(farm.EdibleCrop), GrowingCells: countFact(farm.GrowingCells)}
		for _, definition := range definitions {
			if farm.Crop != nil && definition.Name == farm.GetCrop() {
				row.GrowDays = definition.GrowDays
				row.HarvestNutrition = definition.HarvestNutrition
				row.Demand = definition.NutritionDemandPerDay
				break
			}
		}
		rows = append(rows, row)
	}
	return domain.Known(rows)
}

// ApplyFieldBudget uses the configured reserve target at the review boundary.
func (p *ColonyProjection) ApplyFieldBudget(reserveDays float64) {
	p.Facts.FieldCoverage = policy.FieldCoverage(p.Facts.Colonists, p.FieldCrops, reserveDays)
}

func colonyProduction(v *o.ColonyFactsSnapshot, facts *policy.RoutineFacts) {
	if !hasIssue(v.Issues, "farms") {
		var growing int64
		known := true
		for _, farm := range v.Farms {
			if farm.EdibleCrop == nil {
				known = false
				continue
			}
			if !farm.GetEdibleCrop() {
				continue
			}
			if farm.GrowingCells == nil {
				known = false
				continue
			}
			growing += int64(farm.GetGrowingCells())
		}
		if known {
			facts.GrowingCells = domain.Known(growing)
		}
	}
	if !hasIssue(v.Issues, "cooking") {
		ready, known := false, true
		for _, bench := range v.Cooking {
			if bench.Usable == nil {
				known = false
				continue
			}
			if !bench.GetUsable() {
				continue
			}
			recipes := map[string]bool{}
			for _, recipe := range bench.Recipes {
				recipes[recipe.Recipe.GetDefName()] = true
			}
			for _, bill := range bench.Bills {
				if !recipes[bill.Recipe.GetDefName()] {
					continue
				}
				if bill.Suspended == nil {
					known = false
				} else if !bill.GetSuspended() {
					ready = true
				}
			}
		}
		if ready || known {
			facts.Cooking = domain.Known(ready)
		}
	}
}
