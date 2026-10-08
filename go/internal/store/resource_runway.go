package store

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// resourceRunways forecasts every resource with a configured target from the
// recurring spend native observed (Facts.ResourceConsumption).
func resourceRunways(r RoundsRequest) []policy.ResourceRunway {
	reserves := r.Policy.RunwayReserves(r.Facts.Items, r.Facts.Colonists)
	for resource, reserve := range policy.DrugRunwayReserves(r.Facts.Items, r.Facts.DrugUsers) {
		reserves[resource] = reserve
	}
	targets := make([]policy.Resource, 0, len(reserves))
	for resource := range reserves {
		targets = append(targets, resource)
	}
	sort.Slice(targets, func(i, j int) bool { return targets[i] < targets[j] })
	var result []policy.ResourceRunway
	for _, resource := range targets {
		stock := domain.Unknown[int64]()
		if rows, known := r.Facts.Resources.Value(); known {
			var n int64
			valid := true
			for _, row := range rows {
				if row.Resource == resource {
					if row.Count < 0 || row.Count > math.MaxInt64-n {
						valid = false
						break
					}
					n += row.Count
				}
			}
			if valid {
				stock = domain.Known(n)
			}
		}
		result = append(result, policy.ForecastResourceRunway(resource, stock, r.Facts.ResourceSurfaceOre[resource], reserves[resource], r.Tick, r.Facts.ResourceConsumption))
	}
	return result
}
