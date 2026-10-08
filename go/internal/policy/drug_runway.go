package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Drug runway (#2380, epic #1856). A social drug is spent where a colonist
// takes it; the realized-consumption ring (#2441) counts each dose, and
// ForecastResourceRunway turns the observed rate and the stock into days left.
// The shortfall is the projector's Drugs domain; the stock to hold is the
// runway's own Target, raised into the resource ladder like any other
// runway. The runway exists only once Brewing is finished.

// SocialDrugs are the social drugs the colony brews or rolls.
var SocialDrugs = []Resource{"Beer", "SmokeleafJoint"}

// DrugRunwayReserves are the runway keys of the social drugs: none until
// Brewing is finished, then each at one dose per colonist whose drug policy
// permits it (users), so a drug nobody has taken yet is still stocked; once
// doses are taken the observed rate takes over.
func DrugRunwayReserves(research domain.Fact[ResearchFacts], users map[Resource]int64) map[Resource]int64 {
	if !BrewingFinished(research) {
		return nil
	}
	out := make(map[Resource]int64, len(SocialDrugs))
	for _, drug := range SocialDrugs {
		out[drug] = colonistReserve(users[drug], 1)
	}
	return out
}

// DrugUsers counts, for each social drug, the colonists holding a drug policy
// that allows it (joy, addiction or scheduled use).
func DrugUsers(policies []DrugPolicyEntry) map[Resource]int64 {
	out := map[Resource]int64{}
	for _, p := range policies {
		for _, entry := range p.Entries {
			if drug := Resource(entry.Drug); !entry.Off() && slices.Contains(SocialDrugs, drug) {
				out[drug] += int64(len(p.Pawns))
			}
		}
	}
	return out
}

// DrugResourceRunway is one social drug's runway: the observed use a day, the
// stock and the days of use that stock covers (zero when none is observed).
type DrugResourceRunway struct {
	Resource      Resource
	PerDay        float64
	Stock         int64
	StockDays     float64
	ShortfallDays float64
}

// DrugProjection is the drug domain of the forward projection.
// ShortfallDays is the largest per-drug shortfall.
type DrugProjection struct {
	Resources     []DrugResourceRunway
	ShortfallDays float64
}

// PlanDrugRunway reads the social drugs' rows out of the resource runways. No
// drug row (Brewing unfinished) or a drug whose use or stock is unobserved
// leaves the projection unknown; a drug observed unused has no shortfall.
func PlanDrugRunway(rows []ResourceRunway) domain.Fact[DrugProjection] {
	out := DrugProjection{}
	for _, row := range rows {
		if !slices.Contains(SocialDrugs, row.Resource) {
			continue
		}
		rate, rk := row.ConsumptionPerDay.Value()
		stock, sk := row.Stock.Value()
		if !rk || !sk || !finite(rate) || rate < 0 {
			return domain.Unknown[DrugProjection]()
		}
		entry := DrugResourceRunway{Resource: row.Resource, PerDay: rate, Stock: stock}
		if rate > 0 {
			entry.StockDays = float64(stock) / rate
			entry.ShortfallDays = RunwayShortfall(entry.StockDays, ProjectionHorizonDays)
		}
		out.ShortfallDays = math.Max(out.ShortfallDays, entry.ShortfallDays)
		out.Resources = append(out.Resources, entry)
	}
	if len(out.Resources) == 0 {
		return domain.Unknown[DrugProjection]()
	}
	sort.Slice(out.Resources, func(i, j int) bool { return out.Resources[i].Resource < out.Resources[j].Resource })
	return domain.Known(out)
}
