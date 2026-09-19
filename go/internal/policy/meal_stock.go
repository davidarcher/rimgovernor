package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// StockIngredientChannels report accessible ingredients without inventing a
// replenishment rate or counting stored nutrition twice in FoodPlan.
func StockIngredientChannels(s FoodSupply) []FoodChannel {
	amounts := map[FoodChannelKind]float64{}
	for _, stock := range s.Stocks {
		class, ck := stock.RawClass.Value()
		amount, ak := stock.Nutrition.Value()
		holder, hk := stock.Holder.Value()
		// Human stock belongs to its gated routing row; it cannot certify
		// protein availability for an unrestricted shared meal bill.
		if !ck || !ak || !hk || holder != "" || stock.Reserve || stock.Corpse || stock.IsHumanMeat || amount <= 0 {
			continue
		}
		var kind FoodChannelKind
		switch class {
		case IngredientMeat:
			kind = FoodHunt
		case IngredientVegetable:
			kind = FoodCrop
		case IngredientAnimalProduct:
			kind = FoodAnimalProduct
		default:
			continue
		}
		amounts[kind] += amount
	}
	var rows []FoodChannel
	for _, kind := range []FoodChannelKind{FoodCrop, FoodHunt, FoodAnimalProduct} {
		if amount := amounts[kind]; amount > 0 {
			rows = append(rows, FoodChannel{Kind: kind, ID: "stored-ingredients", NutritionPerDay: domain.Known(0.0), WorkPerDay: domain.Known(0.0), LeadDays: domain.Known(0.0), Open: domain.Known(true), Terms: []FoodPlanTerm{{Name: "accessible_ingredient_nutrition", Value: amount}}})
		}
	}
	return rows
}

func mealStockAvailable(c FoodChannel) bool {
	if c.ID != "stored-ingredients" {
		return false
	}
	for _, t := range c.Terms {
		if t.Name == "accessible_ingredient_nutrition" && fieldPositive(t.Value) {
			return true
		}
	}
	return false
}

func ObservedMealTier(benches []ProductionBench) MealTier {
	best := MealSimple
	for _, bench := range benches {
		for _, bill := range bench.Bills {
			if !positive(bill.Active) {
				continue
			}
			for _, recipe := range bench.Recipes {
				if recipe.Name != bill.Recipe {
					continue
				}
				mood, known := recipe.Mood.Value()
				if !known {
					continue
				}
				tier := MealSimple
				if mood >= 12 {
					tier = MealLavish
				} else if mood >= 5 {
					tier = MealFine
				}
				if mealTierRank(tier) > mealTierRank(best) {
					best = tier
				}
			}
		}
	}
	return best
}
