package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const ResourceRunwayDays = 5.0
const ResourceHistoryWindow domain.Tick = 15 * 60000

// ResourceUse is one journaled placement or completed production batch.
// Unknown quantities retain missing or ambiguous recipe evidence.
type ResourceUse struct {
	Tick     domain.Tick
	Resource Resource
	Count    domain.Fact[int64]
}

type ResourceHistory struct {
	Start, End domain.Tick
	Uses       []ResourceUse
}

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

func ForecastResourceRunway(resource Resource, stock, ore domain.Fact[int64], reserve int64, history ResourceHistory) ResourceRunway {
	out := ResourceRunway{Resource: resource, Tick: history.End, Reserve: reserve, Stock: stock, SurfaceOre: ore}
	start := max(history.Start, history.End-ResourceHistoryWindow)
	if start < 0 || history.End <= start || reserve < 0 || reserve > 10000 {
		return out
	}
	out.WindowDays = float64(history.End-start) / 60000
	// Less than one day is too little evidence for a maintenance rate.
	if out.WindowDays < 1 {
		return out
	}
	var consumed int64
	for _, use := range history.Uses {
		if use.Resource != resource || use.Tick <= start || use.Tick > history.End {
			continue
		}
		n, known := use.Count.Value()
		if !known || n < 0 || n > math.MaxInt64-consumed {
			return out
		}
		consumed += n
	}
	rate := float64(consumed) / out.WindowDays
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

// RecipeResourceUse counts only quantities identical across every allowed
// alternative in each ingredient slot. It never guesses which stuff was used.
func RecipeResourceUse(resource Resource, ingredients domain.Fact[[][]Amount], filter []string, iterations uint32) domain.Fact[int64] {
	slots, known := ingredients.Value()
	if !known || iterations == 0 {
		return domain.Unknown[int64]()
	}
	var total int64
	for _, slot := range slots {
		var amount int64
		seen := false
		for _, choice := range slot {
			allowed := len(filter) == 0
			for _, name := range filter {
				allowed = allowed || name == string(choice.Resource)
			}
			if !allowed {
				continue
			}
			n := int64(0)
			if choice.Resource == resource {
				n = choice.Count
			}
			if n < 0 || seen && amount != n {
				return domain.Unknown[int64]()
			}
			amount, seen = n, true
		}
		if !seen || amount > math.MaxInt64-total {
			return domain.Unknown[int64]()
		}
		total += amount
	}
	if total > math.MaxInt64/int64(iterations) {
		return domain.Unknown[int64]()
	}
	return domain.Known(total * int64(iterations))
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
		if source.Method != ResourceSourceMine || source.Safety != "open_surface" {
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
