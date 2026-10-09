package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Material runway: every runway row that is neither a
// drug nor a medicine (steel and components among them) against the
// fabrication, construction and gear burn the realized-consumption ring
// counts. The stock to hold is the runway's own Target; PlanSupply
// prices a bill's steel draw against the line the runway protects.

// PlanMaterialRunway reads every runway row that is neither a drug nor a
// medicine out of the resource runways. A missing row or an unobserved use or
// stock leaves the projection unknown.
func PlanMaterialRunway(rows []ResourceRunway, items ItemFacts) domain.Fact[StockProjection] {
	return stockProjection(rows, func(r Resource) bool {
		for _, d := range items.Drugs {
			if d.Def == r {
				return false
			}
		}
		return !items.IsMedicine(r)
	})
}
