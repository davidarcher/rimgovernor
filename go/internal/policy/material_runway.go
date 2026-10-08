package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Material runway (#2381, epic #1856): steel and components against the
// fabrication, construction and gear burn the realized-consumption ring
// (#2441) counts. The stock to hold is the runway's own Target; PlanSupply
// prices a bill's steel draw against the line the runway protects.

// MaterialResources are the materials whose runway is projected: steel and
// the components fabricated from it.
var MaterialResources = []Resource{"Steel", ComponentResource}

// MaterialProjection is the resources domain of the forward projection.
// ShortfallDays is the largest per-material shortfall.
type MaterialProjection struct {
	Resources     []DrugResourceRunway
	ShortfallDays float64
}

// PlanMaterialRunway reads the materials' rows out of the resource runways.
// A missing row or an unobserved use or stock leaves the projection unknown.
func PlanMaterialRunway(rows []ResourceRunway) domain.Fact[MaterialProjection] {
	resources, shortfall, ok := stockRunways(rows, MaterialResources)
	if !ok {
		return domain.Unknown[MaterialProjection]()
	}
	return domain.Known(MaterialProjection{Resources: resources, ShortfallDays: shortfall})
}
