package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Drug runway. A social drug is spent where a colonist
// takes it; the realized-consumption ring counts each dose, and
// ForecastResourceRunway turns the observed rate and the stock into days left.
// The shortfall is the projector's Drugs domain; the stock to hold is the
// runway's own Target, raised into the resource ladder like any other
// runway. The social drugs are the catalog's (ItemFacts.RecreationDrugs).

// DrugRunwayReserves are the runway keys of the catalog's social drugs, each
// at one dose per colonist whose drug policy permits it (users), so a drug
// nobody has taken yet is still stocked; once doses are taken the observed
// rate takes over.
func DrugRunwayReserves(items ItemFacts, users map[Resource]int64) map[Resource]int64 {
	drugs := items.RecreationDrugs()
	out := make(map[Resource]int64, len(drugs))
	for _, drug := range drugs {
		out[drug.Def] = max(0, users[drug.Def])
	}
	return out
}

// DrugUsers counts, for each social drug, the colonists holding a drug policy
// that allows it (joy, addiction or scheduled use).
func DrugUsers(items ItemFacts, policies []DrugPolicyEntry) map[Resource]int64 {
	social := items.RecreationDrugs()
	out := map[Resource]int64{}
	for _, p := range policies {
		for _, entry := range p.Entries {
			drug := Resource(entry.Drug)
			if !entry.Off() && slices.ContainsFunc(social, func(d Drug) bool { return d.Def == drug }) {
				out[drug] += int64(len(p.Pawns))
			}
		}
	}
	return out
}

// StockRunway is one resource's runway: the observed use a day, the stock and
// the days of use that stock covers (zero when none is observed).
type StockRunway struct {
	Resource      Resource
	PerDay        float64
	Stock         int64
	StockDays     float64
	ShortfallDays float64
}

// StockProjection is one resource-runway domain of the forward projection
// (drugs, materials, medicines). ShortfallDays is the largest per-resource
// shortfall.
type StockProjection struct {
	Resources     []StockRunway
	ShortfallDays float64
}

// PlanDrugRunway reads the social drugs' rows out of the resource runways. No
// drug row or a drug whose use or stock is unobserved leaves the projection
// unknown; a drug observed unused has no shortfall.
func PlanDrugRunway(rows []ResourceRunway, items ItemFacts) domain.Fact[StockProjection] {
	social := items.RecreationDrugs()
	return stockProjection(rows, func(r Resource) bool {
		return slices.ContainsFunc(social, func(d Drug) bool { return d.Def == r })
	})
}

// stockProjection is the runway of each row the predicate keeps: the observed
// use a day, the stock and the days it covers, and the largest shortfall. No
// such row, or one whose use or stock is unobserved, is unknown.
func stockProjection(rows []ResourceRunway, keep func(Resource) bool) domain.Fact[StockProjection] {
	var out StockProjection
	for _, row := range rows {
		if !keep(row.Resource) {
			continue
		}
		rate, rk := row.ConsumptionPerDay.Value()
		stock, sk := row.Stock.Value()
		if !rk || !sk || !finite(rate) || rate < 0 {
			return domain.Unknown[StockProjection]()
		}
		entry := StockRunway{Resource: row.Resource, PerDay: rate, Stock: stock}
		if rate > 0 {
			entry.StockDays = float64(stock) / rate
			entry.ShortfallDays = RunwayShortfall(entry.StockDays, ProjectionHorizonDays)
		}
		out.ShortfallDays = math.Max(out.ShortfallDays, entry.ShortfallDays)
		out.Resources = append(out.Resources, entry)
	}
	if len(out.Resources) == 0 {
		return domain.Unknown[StockProjection]()
	}
	sort.Slice(out.Resources, func(i, j int) bool { return out.Resources[i].Resource < out.Resources[j].Resource })
	return domain.Known(out)
}
