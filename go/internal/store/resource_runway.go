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
	reserves := map[policy.Resource]int64{}
	for resource, reserve := range r.Policy.ResourceTargets {
		reserves[resource] = reserve
	}
	// A social drug has no ore to mine: its prospective supply is observed zero.
	drugs := policy.DrugRunwayReserves(r.Facts.Research)
	for resource, reserve := range drugs {
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
		ore := r.Facts.ResourceSurfaceOre[resource]
		if _, drug := drugs[resource]; drug {
			ore = domain.Known(int64(0))
		}
		result = append(result, policy.ForecastResourceRunway(resource, stock, ore, reserves[resource], r.Tick, r.Facts.ResourceConsumption))
	}
	return result
}
