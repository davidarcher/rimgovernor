package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FieldCrop budgets a complete growth cycle plus stored-food reserve. Growing
// cells describe production capacity, never food available for consumption.
type FieldCrop struct {
	Edible                             domain.Fact[bool]
	GrowingCells                       domain.Fact[int64]
	Demand, GrowDays, HarvestNutrition domain.Fact[float64]
}

func FieldCoverage(colonists domain.Fact[int64], crops domain.Fact[[]FieldCrop], reserveDays float64) domain.Fact[float64] {
	rows, known := crops.Value()
	if !known || !fieldPositive(reserveDays) {
		return domain.Unknown[float64]()
	}
	coverage := 0.0
	for _, crop := range rows {
		edible, known := crop.Edible.Value()
		if !known {
			return domain.Unknown[float64]()
		}
		if !edible {
			continue
		}
		count, ck := colonists.Value()
		cells, sk := crop.GrowingCells.Value()
		demand, dk := crop.Demand.Value()
		days, gk := crop.GrowDays.Value()
		yield, yk := crop.HarvestNutrition.Value()
		if !ck || count < 0 || !sk || cells < 0 || !dk || !gk || !yk || !fieldPositive(demand) || !fieldPositive(days) || !fieldPositive(yield) {
			return domain.Unknown[float64]()
		}
		target := fieldCoverageTarget(count, demand, days, yield, reserveDays)
		if !fieldPositive(target) {
			return domain.Unknown[float64]()
		}
		coverage += float64(cells) / target
	}
	if math.IsInf(coverage, 0) || math.IsNaN(coverage) {
		return domain.Unknown[float64]()
	}
	return domain.Known(coverage)
}

func fieldPositive(v float64) bool { return v > 0 && !math.IsNaN(v) && !math.IsInf(v, 0) }

func fieldCoverageTarget(count int64, demand, days, yield, reserveDays float64) float64 {
	return math.Max(float64(count)*10, math.Ceil(demand*(days*2.5+reserveDays)/yield))
}

// FieldCapacityTarget is the cell count at which FieldCoverage reads 1 for
// the crop: growth stops there, so a shrink target below it would trim cells
// the planner keeps adding.
func FieldCapacityTarget(colonists domain.Fact[int64], crop CropChoice, reserve float64) domain.Fact[int] {
	count, ck := colonists.Value()
	demand, dk := crop.Demand.Value()
	days, gk := crop.GrowDays.Value()
	yield, yk := crop.HarvestNutrition.Value()
	if !ck || count < 0 || !dk || !gk || !yk || !fieldPositive(demand) || !fieldPositive(days) || !fieldPositive(yield) || !fieldPositive(reserve) {
		return domain.Unknown[int]()
	}
	target := fieldCoverageTarget(count, demand, days, yield, reserve)
	if !fieldPositive(target) || target > 65536 {
		return domain.Unknown[int]()
	}
	return domain.Known(int(target))
}
