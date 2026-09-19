package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CaravanFoodStock is a native transfer group eligible for the entire crew.
// Nutrition is per unit; RotDays is remaining shelf life outside refrigeration.
type CaravanFoodStock struct {
	GroupID    string
	Count      int64
	Nutrition  float64
	Perishable bool
	RotDays    domain.Fact[float64]
	Reserve    bool
}

type CaravanFoodCargo struct {
	GroupID string
	Count   int64
}

// SelectCaravanFood packs the journey's demand from food that survives it,
// taking reserve stock first and then the longest remaining shelf life.
// A shortfall returns no selection so callers cannot mistake it for a safe pack.
func SelectCaravanFood(stocks []CaravanFoodStock, demandPerDay, journeyDays float64) ([]CaravanFoodCargo, bool) {
	if len(stocks) > 256 || !fieldPositive(demandPerDay) || !fieldPositive(journeyDays) {
		return nil, false
	}
	remaining := demandPerDay * journeyDays
	if !fieldPositive(remaining) {
		return nil, false
	}
	rows := append([]CaravanFoodStock(nil), stocks...)
	seen := map[string]bool{}
	shelf := map[string]float64{}
	for _, row := range rows {
		if !foodID(row.GroupID) || seen[row.GroupID] || row.Count < 0 || row.Count > math.MaxInt32 || !fieldPositive(row.Nutrition) {
			return nil, false
		}
		seen[row.GroupID] = true
		if !row.Perishable {
			shelf[row.GroupID] = math.Inf(1)
			continue
		}
		if days, known := row.RotDays.Value(); known && foodNumber(days) {
			shelf[row.GroupID] = days
		}
	}
	sort.Slice(rows, func(i, j int) bool {
		a, b := rows[i], rows[j]
		if a.Reserve != b.Reserve {
			return a.Reserve
		}
		if shelf[a.GroupID] != shelf[b.GroupID] {
			return shelf[a.GroupID] > shelf[b.GroupID]
		}
		return a.GroupID < b.GroupID
	})
	var result []CaravanFoodCargo
	for _, row := range rows {
		if shelf[row.GroupID] <= journeyDays || row.Count == 0 {
			continue
		}
		needed := math.Ceil(remaining / row.Nutrition)
		count := row.Count
		if needed < float64(count) {
			count = int64(needed)
		}
		result = append(result, CaravanFoodCargo{row.GroupID, count})
		remaining -= float64(count) * row.Nutrition
		if remaining <= 0 {
			return result, true
		}
	}
	return nil, false
}
