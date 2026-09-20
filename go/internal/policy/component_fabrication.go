package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ComponentFabricationTarget budgets 12 steel per new component, keeping the
// steel reserve plus five days of consumption in actual stock. Prospective ore
// cannot fund fabrication, even when it makes the steel forecast healthy.
// A known zero means no bill can be funded; missing evidence remains unknown.
func ComponentFabricationTarget(target int64, stock domain.Fact[[]Amount], supply []Stock, runways []ResourceRunway) (int64, bool) {
	rows, known := stock.Value()
	if !known || target <= 0 || target > 10000 {
		return 0, false
	}
	have, steel := int64(0), int64(0)
	for _, row := range rows {
		if row.Count < 0 {
			return 0, false
		}
		switch row.Resource {
		case ComponentResource:
			have = row.Count
		case "Steel":
			steel = row.Count
		}
	}
	var components, budget *ResourceRunway
	for i := range runways {
		switch runways[i].Resource {
		case ComponentResource:
			components = &runways[i]
		case "Steel":
			budget = &runways[i]
		}
	}
	if components == nil || budget == nil {
		return 0, false
	}
	deficit, known := components.Deficit.Value()
	if !known {
		return 0, false
	}
	if !deficit || have >= target {
		return 0, true
	}
	rate, known := budget.ConsumptionPerDay.Value()
	if !known || math.IsNaN(rate) || math.IsInf(rate, 0) || rate < 0 || budget.Reserve < 0 {
		return 0, false
	}
	// Supply is the fresh usable ingredient census, which may be lower than
	// colony stock. Never spend inaccessible steel or borrow from the floor.
	available := int64(0)
	for _, row := range supply {
		if row.Resource == "Steel" {
			var ok bool
			available, ok = row.Available.Value()
			if !ok || available < 0 {
				return 0, false
			}
		}
	}
	steel = min(steel, available)
	floor := float64(budget.Reserve) + math.Ceil(rate*ResourceRunwayDays)
	if floor >= float64(steel) {
		return 0, true
	}
	count := min(target-have, (steel-int64(floor))/12)
	if count <= 0 {
		return 0, true
	}
	return have + count, true
}
