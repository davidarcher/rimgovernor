package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResourceRunway separates immediately usable stock from prospective ore.
// DaysLeft includes only known safe surface ore; unknown ore counts as none.
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
// consumption window (resource_consumption.go). A window shorter than a day
// is read as a day, so a burst in the first hours is not a huge daily rate.
// Unknown consumption leaves the rate, and so the runway, unknown; unknown
// ore is no prospective supply.
func ForecastResourceRunway(resource Resource, stock, ore domain.Fact[int64], reserve int64, tick domain.Tick, consumption domain.Fact[ResourceConsumption]) ResourceRunway {
	out := ResourceRunway{Resource: resource, Tick: tick, Reserve: reserve, Stock: stock, SurfaceOre: ore}
	c, known := consumption.Value()
	if !known || reserve < 0 {
		return out
	}
	out.WindowDays = c.WindowDays
	rate := float64(c.Recurring[resource]) / math.Max(c.WindowDays, 1)
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
	remaining, _ := ore.Value()
	days := (float64(usable) + float64(max(0, remaining))) / rate
	out.DaysLeft = domain.Known(days)
	out.Deficit = domain.Known(days < ProjectionHorizonDays)
	if days < ProjectionHorizonDays {
		out.Target, _ = out.ProtectedLine()
	}
	return out
}

// ProtectedLine is the stock the runway guards: the reserve plus the
// observed rate over the projection horizon. It is the target while the
// runway is in deficit and the trade's retained stock always. False while
// the rate is unread.
func (r ResourceRunway) ProtectedLine() (int64, bool) {
	rate, known := r.ConsumptionPerDay.Value()
	if !known || r.Reserve < 0 {
		return 0, false
	}
	return r.Reserve + int64(math.Ceil(rate*ProjectionHorizonDays)), true
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

// RunwayReserves is every resource a runway is forecast for and its reserve:
// every catalog medicine (the colony tends with whichever it holds, so a
// better medicine in use is counted too), the lowest-potency one (herbal)
// reserved at the medical reserve's TargetPerColonist doses per colonist so a
// colony never tended still stocks it; once doses are used the observed tend
// rate takes over. Without the catalog's medicines it adds nothing; an
// unread colonist count leaves the reserve zero. Every other resource the
// consumption ring shows recurring spend on (MaterialRunwayKeys) is a row with
// no reserve: its protected line is the observed rate over the horizon.
func (p RoundsPolicy) RunwayReserves(items ItemFacts, colonists domain.Fact[int64], consumption domain.Fact[ResourceConsumption]) map[Resource]int64 {
	materials := MaterialRunwayKeys(items, consumption)
	out := make(map[Resource]int64, len(items.MedicalPotency)+len(materials))
	for rank, medicine := range items.MedicineTiers() {
		out[medicine] = 0
		if rank == 0 {
			n, _ := colonists.Value()
			out[medicine] = max(0, n) * p.MedicalReserve.TargetPerColonist
		}
	}
	for _, resource := range materials {
		if _, set := out[resource]; !set {
			out[resource] = 0
		}
	}
	return out
}

// MaterialRunwayKeys are the resources the consumption ring shows recurring
// spend on, sorted: the colony's own use names them, no list does. Food (a
// catalog nutrition value) is left to the food ledger, which plans it.
func MaterialRunwayKeys(items ItemFacts, consumption domain.Fact[ResourceConsumption]) []Resource {
	c, known := consumption.Value()
	if !known {
		return nil
	}
	var out []Resource
	for resource, spent := range c.Recurring {
		if spent > 0 && items.Nutrition[resource] <= 0 {
			out = append(out, resource)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
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
