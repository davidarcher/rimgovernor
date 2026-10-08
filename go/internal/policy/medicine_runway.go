package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Medicine runway (#2378, epic #1856). The reserve medicine (herbal) is spent
// where a colonist is tended; the realized-consumption ring (#2441) counts each
// dose and ForecastResourceRunway turns the observed rate and the stock into
// days left. The shortfall is the projector's Medicine domain; the stock to
// hold is the runway's own Target.

// MedicineInputs are the resource runways and the catalog the medicine domain
// reads its reserve medicine out of.
type MedicineInputs struct {
	Runways []ResourceRunway
	Items   ItemFacts
}

// MedicineProjection is the medicine domain of the forward projection.
type MedicineProjection struct {
	Resource      Resource
	PerDay        float64
	Stock         int64
	StockDays     float64
	ShortfallDays float64
}

// PlanMedicineRunway reads the reserve medicine's row out of the resource
// runways. No catalog medicine, no row, or an unobserved use or stock leaves the
// projection unknown; a medicine observed unused has no shortfall.
func PlanMedicineRunway(in MedicineInputs) domain.Fact[MedicineProjection] {
	herbal, err := in.Items.MedicineAt(0)
	if err != nil {
		return domain.Unknown[MedicineProjection]()
	}
	for _, row := range in.Runways {
		if row.Resource != herbal {
			continue
		}
		rate, rk := row.ConsumptionPerDay.Value()
		stock, sk := row.Stock.Value()
		if !rk || !sk || !finite(rate) || rate < 0 {
			return domain.Unknown[MedicineProjection]()
		}
		out := MedicineProjection{Resource: herbal, PerDay: rate, Stock: stock}
		if rate > 0 {
			out.StockDays = float64(stock) / rate
			out.ShortfallDays = RunwayShortfall(out.StockDays, ProjectionHorizonDays)
		}
		return domain.Known(out)
	}
	return domain.Unknown[MedicineProjection]()
}

// colonistReserve is per doses for each of count colonists, clamped to the
// runway's ceiling.
func colonistReserve(count, per int64) int64 {
	if count <= 0 || per <= 0 {
		return 0
	}
	if count > 10000/per {
		return 10000
	}
	return count * per
}
