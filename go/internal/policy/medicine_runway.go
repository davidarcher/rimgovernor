package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Medicine runway. Medicine is spent where a colonist is
// tended; the realized-consumption ring counts each dose and
// ForecastResourceRunway turns the observed rate and the stock into days left.
// Every catalog medicine has a row, so a better medicine in use counts. The
// shortfall is the projector's Medicine domain; the stock to hold is the
// runway's own Target.

// PlanMedicineRunway reads the catalog medicines' rows out of the resource
// runways. No catalog medicine, no row, or an unobserved use or stock leaves
// the projection unknown; a medicine observed unused has no shortfall.
func PlanMedicineRunway(rows []ResourceRunway, items ItemFacts) domain.Fact[StockProjection] {
	return stockProjection(rows, items.IsMedicine)
}
