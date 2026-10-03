package policy

import (
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const DefaultFoodReserveDays = 5.0

func ReserveFoodDefinition(def Resource) bool {
	return def == "MealSurvivalPack" || def == "Pemmican"
}

// DropReserveHeld removes supply rows the food reserve owns, so the supplies
// planner never re-allows reserve food MaintainFoodStorage forbade: a stack
// the reserve is about to hold, or a forbidden reserve-food stack it keeps
// (anything outside Release). Without it both planners flipped the same
// Pemmican stack forever, each ending "no longer has the requested forbid
// state".
func DropReserveHeld(rows []StartingSupply, reserve domain.Fact[FoodReserveReview]) []StartingSupply {
	review, known := reserve.Value()
	if !known {
		return rows
	}
	hold, release := map[string]bool{}, map[string]bool{}
	for _, id := range review.Hold {
		hold[id] = true
	}
	for _, id := range review.Release {
		release[id] = true
	}
	var out []StartingSupply
	for _, row := range rows {
		if hold[row.Thing] || !row.Forbid && !release[row.Thing] && ReserveFoodDefinition(Resource(row.Definition)) {
			continue
		}
		out = append(out, row)
	}
	return out
}

// FoodReserveReview proposes stock IDs for the shared supply-action planner.
// A proposal is not a forbid write or evidence that food has been produced.
type FoodReserveReview struct {
	TargetNutrition, StockNutrition, DeficitNutrition float64
	ByDefinition                                      map[Resource]float64
	Hold, Release                                     []string
	Emergency                                         bool
	// Short: the runway without reserve food is under the seasonal minimum,
	// so the reserve is food to eat, not insurance to keep.
	Short bool
}

// ReviewFoodReserve counts shared reserve foods, roofed or not, at observed nutrition.
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
	arriving := false
	for _, lead := range deliveries {
		if !foodNumber(lead) {
			return FoodReserveReview{}, ErrFoodFacts
		}
		arriving = arriving || lead < days
	}
	r.Short = known && days < minimumDays
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
		// The reserve is surplus insurance: below the seasonal minimum
		// (reserve food excluded) every held stack is released to eat.
		if r.Emergency || known && days < minimumDays {
			if stock.Reserve {
				r.Release = append(r.Release, stock.ID)
			}
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
		if !stock.Reserve && known && days >= minimumDays {
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

// ReserveBillRunning reports an active bill for a reserve product while the
// reserve is short: the product it makes needs game time, not another method.
func ReserveBillRunning(benches domain.Fact[[]ProductionBench], reserve FoodReserveReview) bool {
	rows, known := benches.Value()
	if !known || reserve.Emergency || !fieldPositive(reserve.DeficitNutrition) {
		return false
	}
	for _, bench := range rows {
		products := map[string]bool{}
		for _, recipe := range bench.Recipes {
			if len(recipe.Products) == 1 && ReserveFoodDefinition(Resource(recipe.Products[0].Name)) {
				products[recipe.Name] = true
			}
		}
		for _, bill := range bench.Bills {
			if active, ak := bill.Active.Value(); ak && active && products[bill.Recipe] {
				return true
			}
		}
	}
	return false
}

// SelectReserveBill selects a standing native target-count bill. Its target
// includes existing stock of the selected product because native counts that
// stock toward satisfaction; only other reserve products reduce the target.
// The target never exceeds the product's observed storable count.
// No reserve bill is selected while the runway is short (the food is to be
// eaten, not cooked ahead). Matching bills are corrected under Auto;
// unrelated recipes are retained.
func SelectReserveBill(benches domain.Fact[[]ProductionBench], reserve FoodReserveReview) (BillSelection, bool) {
	rows, known := benches.Value()
	if !known || reserve.Emergency || reserve.Short || !fieldPositive(reserve.DeficitNutrition) || !fieldPositive(reserve.TargetNutrition) {
		return BillSelection{}, false
	}
	var options []BillSelection
	products := map[string]Resource{}
	// A recipe whose product is already made by another recipe's bill needs
	// no bill of its own: Make_Pemmican and Make_PemmicanBulk are one reserve.
	produces := map[string]Resource{}
	for _, bench := range rows {
		for _, recipe := range bench.Recipes {
			if len(recipe.Products) == 1 && ReserveFoodDefinition(Resource(recipe.Products[0].Name)) {
				produces[recipe.Name] = Resource(recipe.Products[0].Name)
			}
		}
	}
	for _, bench := range rows {
		usable, uk := bench.Usable.Value()
		token, tk := bench.Token.Value()
		if !uk || !usable || !tk || !foodID(token) || !foodID(bench.ID) || bench.Butcher || len(bench.Bills) > 15 {
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
			// Native cannot finish a bill whose product has nowhere to go:
			// cap the target at what storage accepting it can hold (#1359).
			if storable, sk := product.Storable.Value(); sk && target > float64(storable) {
				target = float64(storable)
			}
			if !fieldPositive(target) || target > 10000 {
				continue
			}
			selected := BillSelection{Bench: bench.ID, Recipe: recipe.Name, Token: token, Mode: domain.FoodTarget, Target: int32(target)}
			exists, siblingBill := false, false
			for _, other := range rows {
				for _, bill := range other.Bills {
					if bill.Recipe != recipe.Name {
						if sibling, ok := produces[bill.Recipe]; ok && sibling == def && !exists {
							siblingBill = true
						}
						continue
					}
					exists = true
					if other.ID == bench.ID && !billAdequate(bill, selected) && foodID(bill.ID) && (selected.Replace == "" || bill.ID < selected.Replace) {
						selected.Replace = bill.ID
					}
				}
			}
			if siblingBill && !exists {
				continue
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
		// The bulk recipe wins over its single-item sibling: singles are for
		// the odd one or two items a player cranks out by hand.
		if bulk := strings.HasSuffix(a.Recipe, "Bulk"); bulk != strings.HasSuffix(b.Recipe, "Bulk") {
			return bulk
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
