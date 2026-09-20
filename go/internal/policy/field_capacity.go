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
		target := math.Max(float64(count)*10, math.Ceil(demand*(days*2.5+reserveDays)/yield))
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

const (
	// FieldModuleCellCount is one field module, the 11x11 grid module
	// interior (#608); FieldHalfModuleCellCount the 11x5 half module.
	FieldModuleCellCount     = int(ColonyGridInterior * ColonyGridInterior)
	FieldHalfModuleCellCount = int(ColonyGridInterior * ColonyGridSubCell)
	// FieldHalfModuleBelow is the demand under which a half module is
	// planted instead of a whole one.
	FieldHalfModuleBelow = 60
)

// FieldModuleCells rounds a cell shortfall up to whole field modules at the
// tiers that plant on the colony grid (#608): under FieldHalfModuleBelow
// cells one half module, otherwise whole modules. A shortfall of 20 cells
// is one 11x5, not a 4x4 and a 2x2.
func FieldModuleCells(needed int) int {
	if needed <= 0 {
		return 0
	}
	if needed < FieldHalfModuleBelow {
		return FieldHalfModuleCellCount
	}
	return (needed + FieldModuleCellCount - 1) / FieldModuleCellCount * FieldModuleCellCount
}

func fieldPositive(v float64) bool { return v > 0 && !math.IsNaN(v) && !math.IsInf(v, 0) }
