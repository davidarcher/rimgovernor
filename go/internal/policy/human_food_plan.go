package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"sort"
)

// HumanFoodChannel routes finite stocks. It deliberately contributes no new
// nutrition/day: the forecast already owns stock, and butchery is conversion.
func HumanFoodChannel(benches []ProductionBench, supply FoodSupply, humans []PawnID, targetDays float64) (FoodChannel, bool) {
	if !foodNumber(targetDays) || targetDays <= 0 {
		return FoodChannel{}, false
	}
	qualified := false
	for _, b := range benches {
		if _, ok := QualifyingHumanButcher(b.HumanButchers); ok {
			qualified = true
		}
	}
	if !qualified {
		return FoodChannel{}, false
	}
	baseline := supply
	baseline.Stocks = nil
	nutrition, vegetables, tradeSurplus := 0., 0., 0.
	for _, stock := range supply.Stocks {
		n, k := stock.Nutrition.Value()
		holder, hk := stock.Holder.Value()
		if !k || !hk || holder != "" {
			continue
		}
		if stock.Corpse {
			forbidden, known := stock.Forbidden.Value()
			if !known || forbidden {
				continue
			}
		}
		if stock.IsHumanlike || stock.IsHumanMeat && (stock.RawMeat || stock.Reserve) {
			if !stock.Reserve {
				nutrition += n
			} else {
				tradeSurplus += n
			}
			continue
		}
		baseline.Stocks = append(baseline.Stocks, stock)
		if stock.Vegetable && !stock.Reserve {
			vegetables += n
		}
	}
	if nutrition <= 0 && tradeSurplus <= 0 {
		return FoodChannel{}, false
	}
	forecast, err := ForecastFood(baseline, nil)
	if err != nil {
		return FoodChannel{}, false
	}
	human := map[PawnID]bool{}
	for _, id := range humans {
		human[id] = true
	}
	accepts := map[PawnID]bool{}
	for _, c := range supply.Consumers {
		accepts[c.ID], _ = c.HumanMeatAcceptable.Value()
	}
	feed, meals := 0., 0.
	for _, c := range forecast.Consumers {
		deficit := math.Max(0, targetDays-c.RunwayDays) * c.NutritionPerDay
		if !human[c.ID] {
			feed += deficit
		} else if accepts[c.ID] {
			meals += deficit
		}
	}
	survival := false
	for _, b := range benches {
		usable, _ := b.Usable.Value()
		if !usable {
			continue
		}
		for _, r := range b.Recipes {
			available, _ := r.Available.Value()
			if !available {
				continue
			}
			for _, p := range r.Products {
				survival = survival || p.Name == "MealSurvivalPack"
			}
		}
	}
	routes, err := RouteHumanMeat(HumanMeatRouting{Nutrition: nutrition, FeedShortfall: feed, EligibleMealDemand: meals, VegetableNutrition: math.Max(0, vegetables-math.Min(feed, nutrition)), CanMakeSurvivalMeals: survival})
	if err != nil {
		return FoodChannel{}, false
	}
	channel := FoodChannel{Kind: FoodCorpse, ID: "human-butchery", NutritionPerDay: domain.Known(0.), WorkPerDay: domain.Known(0.), LeadDays: domain.Known(0.), Open: domain.Known(false), Terms: []FoodPlanTerm{{Name: "finite_human_nutrition", Value: nutrition}}}
	for _, r := range routes {
		channel.Terms = append(channel.Terms, FoodPlanTerm{Name: string(r.Route), Value: r.Nutrition})
	}
	channel.Terms = append(channel.Terms, FoodPlanTerm{Name: "trade_surplus_nutrition", Value: tradeSurplus})
	return channel, true
}

// HumanCookingIngredients is an explicit route filter. Shared colonist meals
// are admitted only when every diner accepts them; mixed colonies keep human
// ingredients in feed and protected trade output.
func HumanCookingIngredients(supply FoodSupply, humans []FoodConsumer, plan domain.Fact[FoodPlan], route HumanMeatRoute) []string {
	if HumanRouteNutrition(plan, route) <= 0 {
		return nil
	}
	if route == HumanMeatMeals {
		if len(humans) == 0 {
			return nil
		}
		for _, c := range humans {
			accept, known := c.HumanMeatAcceptable.Value()
			if !known || !accept {
				return nil
			}
		}
	}
	defs := map[string]bool{}
	meat, veg := false, false
	for _, s := range supply.Stocks {
		holder, hk := s.Holder.Value()
		n, nk := s.Nutrition.Value()
		if !hk || holder != "" || !nk || n <= 0 || s.Corpse || s.Reserve {
			continue
		}
		if s.IsHumanMeat && s.RawMeat || s.Vegetable {
			defs[string(s.DefName)] = true
			meat = meat || s.IsHumanMeat
			veg = veg || s.Vegetable
		}
	}
	if !meat || !veg {
		return nil
	}
	out := make([]string, 0, len(defs))
	for d := range defs {
		if !foodID(d) {
			return nil
		}
		out = append(out, d)
	}
	sort.Strings(out)
	return out
}

func HumanRouteNutrition(plan domain.Fact[FoodPlan], route HumanMeatRoute) float64 {
	p, k := plan.Value()
	if !k {
		return 0
	}
	for _, e := range p.Portfolio {
		if e.Channel.Kind == FoodCorpse && e.Channel.ID == "human-butchery" && e.Decision != FoodPlanClose {
			for _, term := range e.Terms {
				if term.Name == string(route) {
					return term.Value
				}
			}
		}
	}
	return 0
}

func HumanFoodPending(plan domain.Fact[FoodPlan]) bool {
	return HumanRouteNutrition(plan, HumanMeatFeed)+HumanRouteNutrition(plan, HumanMeatSurvivalTrade)+HumanRouteNutrition(plan, HumanMeatRawTrade)+HumanRouteNutrition(plan, HumanMeatMeals) > 0
}

// Explicit human-meat/vegetable membership isolates trade output from ordinary
// meal bills. Native admission verifies every slot and protects sale products.
func SelectHumanSurvivalBill(benches domain.Fact[[]ProductionBench], supply FoodSupply, plan domain.Fact[FoodPlan]) (BillSelection, bool) {
	budget := HumanRouteNutrition(plan, HumanMeatSurvivalTrade)
	if budget <= 0 {
		return BillSelection{}, false
	}
	rows, k := benches.Value()
	if !k {
		return BillSelection{}, false
	}
	defs := map[string]bool{}
	raw := 0.
	veg := 0.
	for _, s := range supply.Stocks {
		holder, hk := s.Holder.Value()
		n, nk := s.Nutrition.Value()
		if !hk || holder != "" || !nk || s.Corpse || s.Reserve {
			continue
		}
		if s.IsHumanMeat && s.RawMeat {
			defs[string(s.DefName)] = true
			raw += n
		} else if s.Vegetable {
			defs[string(s.DefName)] = true
			veg += n
		}
	}
	budget = math.Min(budget, math.Min(raw, veg))
	if budget <= 0 {
		return BillSelection{}, false
	}
	ingredients := make([]string, 0, len(defs))
	for d := range defs {
		if !foodID(d) {
			return BillSelection{}, false
		}
		ingredients = append(ingredients, d)
	}
	sort.Strings(ingredients)
	var choices []BillSelection
	for _, b := range rows {
		usable, uk := b.Usable.Value()
		token, tk := b.Token.Value()
		if !uk || !usable || !tk || len(b.Bills) >= 15 {
			continue
		}
		for _, r := range b.Recipes {
			available, _ := r.Available.Value()
			if !available || len(r.Products) != 1 || r.Products[0].Name != "MealSurvivalPack" {
				continue
			}
			exists := false
			for _, bill := range b.Bills {
				exists = exists || bill.Recipe == r.Name
			}
			if exists {
				continue
			}
			eff, ek := r.NutrientEfficiency.Value()
			pn, pk := r.Products[0].Nutrition.Value()
			if !ek || !pk || !fieldPositive(eff) || !fieldPositive(pn) {
				continue
			}
			target := math.Floor(budget * eff / pn)
			if target < 1 {
				continue
			}
			choices = append(choices, BillSelection{Bench: b.ID, Recipe: r.Name, Token: token, Mode: domain.FoodTarget, Target: int32(math.Min(10000, target)), Ingredients: ingredients})
		}
	}
	sort.Slice(choices, func(i, j int) bool { return choices[i].Bench < choices[j].Bench })
	if len(choices) == 0 {
		return BillSelection{}, false
	}
	return choices[0], true
}
