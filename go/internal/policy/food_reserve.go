package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const DefaultFoodReserveDays = 5.0

func ReserveFoodDefinition(def Resource) bool {
	return def == "MealSurvivalPack" || def == "Pemmican"
}

// FoodReserveReview proposes stock IDs for the shared supply-action planner.
// A proposal is not a forbid write or evidence that food has been produced.
type FoodReserveReview struct {
	TargetNutrition, StockNutrition, DeficitNutrition float64
	ByDefinition                                      map[Resource]float64
	Hold, Release                                     []string
	Emergency                                         bool
}

// ReviewFoodReserve counts roofed shared reserve foods at observed nutrition.
// deliveryDays contains confirmed channel delivery times; unknown channel facts
// cannot authorize releasing stock. The caller supplies the selected home census.
func ReviewFoodReserve(supply FoodSupply, selected []PawnID, reserveDays, minimumDays float64, deliveryDays domain.Fact[[]float64]) (FoodReserveReview, error) {
	// Evaluate the runway without prospective reserve food too. Otherwise
	// releasing the last reserve would immediately make it eligible to hold
	// again, alternating forbid writes while the colony is still short.
	supply.Stocks = append([]FoodStock(nil), supply.Stocks...)
	baseline := supply
	baseline.Stocks = append([]FoodStock(nil), supply.Stocks...)
	for i, stock := range baseline.Stocks {
		if holder, known := stock.Holder.Value(); known && holder == "" && ReserveFoodDefinition(stock.DefName) {
			baseline.Stocks[i].Reserve = true
		}
	}
	forecast, err := ForecastFood(baseline, selected)
	if err != nil || !fieldPositive(reserveDays) || reserveDays > 60 || !fieldPositive(minimumDays) || minimumDays > 60 {
		return FoodReserveReview{}, ErrFoodFacts
	}
	r := FoodReserveReview{ByDefinition: map[Resource]float64{}}
	for _, consumer := range forecast.Consumers {
		r.TargetNutrition += reserveDays * consumer.NutritionPerDay
	}
	if !fieldPositive(r.TargetNutrition) {
		return FoodReserveReview{}, ErrFoodFacts
	}
	days, known := forecast.RunwayDays.Value()
	deliveries, complete := deliveryDays.Value()
	if len(deliveries) > 256 {
		return FoodReserveReview{}, ErrFoodFacts
	}
	arriving := false
	for _, lead := range deliveries {
		if !foodNumber(lead) {
			return FoodReserveReview{}, ErrFoodFacts
		}
		arriving = arriving || lead < days
	}
	r.Emergency = known && complete && days < minimumDays && !arriving
	sort.Slice(supply.Stocks, func(i, j int) bool {
		a, b := supply.Stocks[i], supply.Stocks[j]
		if a.Reserve != b.Reserve {
			return a.Reserve
		}
		return a.ID < b.ID
	})
	for _, stock := range supply.Stocks {
		if !ReserveFoodDefinition(stock.DefName) || stock.IsHumanMeat {
			continue
		}
		holder, hk := stock.Holder.Value()
		if !hk || holder != "" {
			continue
		}
		if r.Emergency {
			if stock.Reserve {
				r.Release = append(r.Release, stock.ID)
			}
			continue
		}
		roofed, rk := stock.Roofed.Value()
		if !rk || !roofed {
			continue
		}
		amount, _ := stock.Nutrition.Value()
		if perishable, _ := stock.Perishable.Value(); perishable {
			ticks, _ := stock.RotTicks.Value()
			if float64(ticks)/60000 <= reserveDays {
				continue
			}
		}
		eligible := false
		for _, eater := range stock.Eaters {
			for _, consumer := range forecast.Consumers {
				eligible = eligible || eater == consumer.ID
			}
		}
		if !eligible {
			continue
		}
		// Supply actions address whole stacks. Hold only enough stacks to
		// cover the target; the final stack may round the reserve upward.
		if !stock.Reserve && r.StockNutrition >= r.TargetNutrition {
			continue
		}
		r.StockNutrition += amount
		r.ByDefinition[stock.DefName] += amount
		if !stock.Reserve && (days >= minimumDays || complete && arriving) {
			r.Hold = append(r.Hold, stock.ID)
		}
	}
	if !foodNumber(r.StockNutrition) {
		return FoodReserveReview{}, ErrFoodFacts
	}
	r.DeficitNutrition = math.Max(0, r.TargetNutrition-r.StockNutrition)
	sort.Strings(r.Hold)
	sort.Strings(r.Release)
	return r, nil
}

// SelectReserveBill selects a standing native target-count bill. Its target
// includes existing stock of the selected product because native counts that
// stock toward satisfaction; only other reserve products reduce the target.
// Matching bills are corrected under Auto; unrelated recipes are retained.
func SelectReserveBill(benches domain.Fact[[]ProductionBench], reserve FoodReserveReview) (BillSelection, bool) {
	rows, known := benches.Value()
	if !known || len(rows) > 256 || reserve.Emergency || !fieldPositive(reserve.DeficitNutrition) || !fieldPositive(reserve.TargetNutrition) {
		return BillSelection{}, false
	}
	var options []BillSelection
	products := map[string]Resource{}
	for _, bench := range rows {
		usable, uk := bench.Usable.Value()
		token, tk := bench.Token.Value()
		if !uk || !usable || !tk || !foodID(token) || !foodID(bench.ID) || bench.Butcher || len(bench.Bills) > 15 || len(bench.Recipes) > 256 {
			continue
		}
		for _, recipe := range bench.Recipes {
			available, ak := recipe.Available.Value()
			if !ak || !available || !foodID(recipe.Name) || len(recipe.Products) != 1 {
				continue
			}
			product := recipe.Products[0]
			def := Resource(product.Name)
			nutrition, nk := product.Nutrition.Value()
			edible, ek := product.Edible.Value()
			if !ReserveFoodDefinition(def) || !nk || !fieldPositive(nutrition) || !ek || !edible {
				continue
			}

			target := math.Ceil((reserve.TargetNutrition - reserve.StockNutrition + reserve.ByDefinition[def]) / nutrition)
			if !fieldPositive(target) || target > 10000 {
				continue
			}
			selected := BillSelection{Bench: bench.ID, Recipe: recipe.Name, Token: token, Mode: domain.FoodTarget, Target: int32(target)}
			exists := false
			for _, other := range rows {
				for _, bill := range other.Bills {
					if bill.Recipe != recipe.Name {
						continue
					}
					exists = true
					if other.ID == bench.ID && !billAdequate(bill, selected) && foodID(bill.ID) && (selected.Replace == "" || bill.ID < selected.Replace) {
						selected.Replace = bill.ID
					}
				}
			}
			if exists && selected.Replace == "" || len(bench.Bills) == 15 && selected.Replace == "" {
				continue
			}
			options = append(options, selected)
			products[recipe.Name] = def
		}
	}
	sort.Slice(options, func(i, j int) bool {
		a, b := options[i], options[j]
		if products[a.Recipe] != products[b.Recipe] {
			return products[a.Recipe] == "MealSurvivalPack"
		}
		if a.Recipe != b.Recipe {
			return a.Recipe < b.Recipe
		}
		return a.Bench < b.Bench
	})
	if len(options) == 0 {
		return BillSelection{}, false
	}
	return options[0], true
}
