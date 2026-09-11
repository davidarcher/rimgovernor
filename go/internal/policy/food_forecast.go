package policy

import (
	"errors"
	"math"
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var ErrFoodFacts = errors.New("food forecast inputs unavailable or invalid")

type FoodConsumer struct {
	ID              PawnID
	NutritionPerDay domain.Fact[float64]
}
type FoodStock struct {
	ID string
	// Known empty holder means shared stock. Unknown ownership is not shared.
	Holder     domain.Fact[PawnID]
	Nutrition  domain.Fact[float64]
	Eaters     []PawnID
	Perishable domain.Fact[bool]
	RotTicks   domain.Fact[int64]
}
type FoodSupply struct {
	Complete  domain.Fact[bool]
	Consumers []FoodConsumer
	Stocks    []FoodStock
}
type ConsumerFoodForecast struct {
	ID                                                               PawnID
	RunwayDays, UsableNutrition, AllocatedNutrition, NutritionPerDay float64
}
type FoodForecast struct {
	RunwayDays                                           domain.Fact[float64]
	UsableNutrition, AtRiskNutrition, InventoryNutrition float64
	Consumers                                            []ConsumerFoodForecast
}

func foodNumber(n float64) bool { return !math.IsNaN(n) && !math.IsInf(n, 0) && n >= 0 }
func foodID(id string) bool {
	return len(id) > 0 && len(id) <= 256 && utf8.ValidString(id) && strings.TrimSpace(id) != "" && !strings.ContainsRune(id, 0)
}

// ForecastFood allocates each stock among its observed eligible consumers by
// demand, then consumes allocations in expiry order. Private inventory belongs
// only to its holder. A selected human census still competes with animal demand.
// This is feasible nutrition under fixed access/temperature, not predicted jobs.
// Nil selected means all consumers; an empty selection has unknown runway.
func ForecastFood(supply FoodSupply, selected []PawnID) (FoodForecast, error) {
	fail := func() (FoodForecast, error) { return FoodForecast{}, ErrFoodFacts }
	if complete, known := supply.Complete.Value(); !known || !complete || len(supply.Consumers) > 256 || len(supply.Stocks) > 4096 {
		return fail()
	}
	demand := map[PawnID]float64{}
	ids := make([]PawnID, 0, len(supply.Consumers))
	for _, consumer := range supply.Consumers {
		rate, known := consumer.NutritionPerDay.Value()
		if _, exists := demand[consumer.ID]; exists || !foodID(string(consumer.ID)) || !known || !foodNumber(rate) {
			return fail()
		}
		demand[consumer.ID] = rate
		ids = append(ids, consumer.ID)
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	var total float64
	for _, id := range ids {
		total += demand[id]
	}
	if !foodNumber(total) || total == 0 {
		return fail()
	}
	wanted := map[PawnID]bool{}
	if selected == nil {
		selected = ids
	}
	for _, id := range selected {
		if _, exists := demand[id]; !exists || wanted[id] {
			return fail()
		}
		wanted[id] = true
	}
	type stock struct {
		id                             string
		holder                         PawnID
		amount, expiry, eligibleDemand float64
		eaters                         map[PawnID]bool
	}
	stocks := make([]stock, 0, len(supply.Stocks))
	seen := map[string]bool{}
	for _, input := range supply.Stocks {
		amount, ak := input.Nutrition.Value()
		holder, hk := input.Holder.Value()
		perishable, pk := input.Perishable.Value()
		if !foodID(input.ID) || seen[input.ID] || !ak || !foodNumber(amount) || !hk || !pk || len(input.Eaters) == 0 || len(input.Eaters) > len(ids) {
			return fail()
		}
		seen[input.ID] = true
		if holder != "" {
			if _, exists := demand[holder]; !exists || len(input.Eaters) != 1 || input.Eaters[0] != holder {
				return fail()
			}
		}
		entry := stock{id: input.ID, holder: holder, amount: amount, expiry: math.Inf(1), eaters: map[PawnID]bool{}}
		if perishable {
			ticks, known := input.RotTicks.Value()
			if !known || ticks < 0 {
				return fail()
			}
			entry.expiry = float64(ticks) / 60000
		}
		for _, id := range input.Eaters {
			if _, exists := demand[id]; !exists || entry.eaters[id] {
				return fail()
			}
			entry.eaters[id] = true
		}
		for _, id := range ids {
			if entry.eaters[id] {
				entry.eligibleDemand += demand[id]
			}
		}
		if !foodNumber(entry.eligibleDemand) {
			return fail()
		}
		stocks = append(stocks, entry)
	}
	sort.Slice(stocks, func(i, j int) bool {
		if stocks[i].expiry != stocks[j].expiry {
			return stocks[i].expiry < stocks[j].expiry
		}
		return stocks[i].id < stocks[j].id
	})
	result := FoodForecast{}
	for _, entry := range stocks {
		if entry.holder != "" && wanted[entry.holder] {
			result.InventoryNutrition += entry.amount
		}
	}
	for _, id := range ids {
		rate := demand[id]
		if rate == 0 || !wanted[id] {
			continue
		}
		row := ConsumerFoodForecast{ID: id, NutritionPerDay: rate}
		for _, entry := range stocks {
			if !entry.eaters[id] {
				continue
			}
			share := entry.amount * (rate / entry.eligibleDemand)
			if entry.holder == id {
				share = entry.amount
			}
			usable := math.Min(share, math.Max(0, entry.expiry-row.RunwayDays)*rate)
			row.AllocatedNutrition += share
			row.UsableNutrition += usable
			row.RunwayDays += usable / rate
		}
		if !foodNumber(row.RunwayDays) || !foodNumber(row.UsableNutrition) || !foodNumber(row.AllocatedNutrition) {
			return fail()
		}
		minimum, known := result.RunwayDays.Value()
		if !known || row.RunwayDays < minimum {
			result.RunwayDays = domain.Known(row.RunwayDays)
		}
		result.UsableNutrition += row.UsableNutrition
		result.AtRiskNutrition += math.Max(0, row.AllocatedNutrition-row.UsableNutrition)
		result.Consumers = append(result.Consumers, row)
	}
	if !foodNumber(result.UsableNutrition) || !foodNumber(result.AtRiskNutrition) || !foodNumber(result.InventoryNutrition) {
		return fail()
	}
	return result, nil
}
