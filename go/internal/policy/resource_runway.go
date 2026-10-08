package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const ResourceRunwayDays = 5.0

// ResourceRunway separates immediately usable stock from prospective ore.
// DaysLeft includes only known safe surface ore; unknown ore leaves it unknown.
// A zero observed rate has no finite runway, rather than an invented infinity.
type ResourceRunway struct {
	Resource                               Resource
	Tick                                   domain.Tick
	WindowDays                             float64
	Reserve                                int64
	Stock, SurfaceOre                      domain.Fact[int64]
	ConsumptionPerDay, StockDays, DaysLeft domain.Fact[float64]
	Deficit                                domain.Fact[bool]
	Target                                 int64
}

// ForecastResourceRunway reads the resource's recurring spend over the
// consumption window (resource_consumption.go). Unknown consumption leaves the
// rate, and so the runway, unknown.
func ForecastResourceRunway(resource Resource, stock, ore domain.Fact[int64], reserve int64, tick domain.Tick, consumption domain.Fact[ResourceConsumption]) ResourceRunway {
	out := ResourceRunway{Resource: resource, Tick: tick, Reserve: reserve, Stock: stock, SurfaceOre: ore}
	c, known := consumption.Value()
	if !known || reserve < 0 || reserve > 10000 {
		return out
	}
	out.WindowDays = c.WindowDays
	// Less than one day is too little evidence for a maintenance rate.
	if out.WindowDays < 1 {
		return out
	}
	rate := float64(c.Recurring[resource]) / out.WindowDays
	out.ConsumptionPerDay = domain.Known(rate)
	n, known := stock.Value()
	if !known || n < 0 {
		return out
	}
	if rate == 0 {
		out.Deficit = domain.Known(n < reserve)
		out.Target = reserve
		return out
	}
	usable := max(0, n-reserve)
	out.StockDays = domain.Known(float64(usable) / rate)
	remaining, known := ore.Value()
	if !known || remaining < 0 || remaining > math.MaxInt64-usable {
		return out
	}
	days := float64(usable+remaining) / rate
	out.DaysLeft = domain.Known(days)
	out.Deficit = domain.Known(days < ResourceRunwayDays)
	if days < ResourceRunwayDays {
		out.Target = int64(math.Min(10000, float64(reserve)+math.Ceil(rate*ResourceRunwayDays)))
	}
	return out
}

func ResourceRunwayTargets(rows []ResourceRunway) map[Resource]int64 {
	out := map[Resource]int64{}
	for _, row := range rows {
		if row.Target > 0 {
			out[row.Resource] = row.Target
		}
	}
	return out
}

func SurfaceOre(sources []ResourceSource) domain.Fact[int64] {
	var total int64
	seen := map[string]bool{}
	for _, source := range sources {
		if source.Method != ResourceSourceMine || !MineSafe(source.Safety) || source.Buried {
			continue
		}
		if seen[source.ThingID] || source.Yield < 0 || source.Yield > math.MaxInt64-total {
			return domain.Unknown[int64]()
		}
		seen[source.ThingID] = true
		total += source.Yield
	}
	return domain.Known(total)
}
