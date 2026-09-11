package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

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
