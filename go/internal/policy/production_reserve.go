package policy

// ReservedFoodNutrition sums the nutrition target already committed to
// existing TargetCount bills. It stays distinct from current edible stock
// (food_forecast.go), animal feed demand (animal_upkeep.go) and future crop
// yield (crop_choice.go, field_capacity.go): a bill still filling toward its
// target reserves that nutrition even though none of it is edible yet, so it
// must never be added to any of those, only netted against new bill demand.
func ReservedFoodNutrition(benches []ProductionBench) (float64, bool) {
	if len(benches) > 256 {
		return 0, false
	}
	total := 0.0
	for _, bench := range benches {
		if bench.Butcher || len(bench.Bills) > 15 || len(bench.Recipes) > 256 {
			continue
		}
		nutrition := map[string]float64{}
		for _, recipe := range bench.Recipes {
			if len(recipe.Products) != 1 {
				continue
			}
			product := recipe.Products[0]
			edible, ek := product.Edible.Value()
			value, nk := product.Nutrition.Value()
			if ek && edible && nk && fieldPositive(value) {
				nutrition[recipe.Name] = value
			}
		}
		for _, bill := range bench.Bills {
			forever, fk := bill.Forever.Value()
			target, tk := bill.TargetCount.Value()
			value, known := nutrition[bill.Recipe]
			if !fk || forever || !tk || target <= 0 || !known {
				continue
			}
			total += float64(target) * value
		}
	}
	if !foodNumber(total) {
		return 0, false
	}
	return total, true
}
