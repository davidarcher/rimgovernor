package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func cookKitchen(usable bool, cooks float64, recipe ProductionRecipe) CropKitchen {
	bench := ProductionBench{ID: "stove", Usable: domain.Known(usable), Recipes: []ProductionRecipe{recipe}}
	return CropKitchen{Benches: domain.Known([]ProductionBench{bench}), Cooks: domain.Known(cooks)}
}

func vegetableMeal(eff, work float64) ProductionRecipe {
	return ProductionRecipe{Name: "CookMealSimple", Role: domain.RoleOrdinaryMeal, Available: domain.Known(true),
		NutrientEfficiency: domain.Known(eff), WorkPerNutrition: domain.Known(work),
		IngredientClasses: domain.Known([]FoodIngredientSlot{{Alternatives: []FoodIngredientClass{IngredientVegetable}}})}
}

func cropField() FoodField {
	return FoodField{ID: "rice", Plan: FieldPlan{Crop: CropChoice{Edible: domain.Known(true), GrowDays: domain.Known(5.0), HarvestNutrition: domain.Known(1.0)}, Sites: FarmSitePlan{Cells: 10}},
		RemainingGrowDays: domain.Known(2.0), WorkPerDay: domain.Known(100.0), Open: domain.Known(false)}
}

func termValue(c FoodChannel, name string) (float64, bool) {
	for _, t := range c.Terms {
		if t.Name == name {
			return t.Value, true
		}
	}
	return 0, false
}

func TestCropCookedBeatsRawWithCookCapacity(t *testing.T) {
	field := cropField() // raw 2/day
	raw := CropChannels([]FoodField{field}, CropKitchen{})[0]
	if n, _ := raw.NutritionPerDay.Value(); n != 2 {
		t.Fatalf("raw %v", n)
	}
	if _, cooked := termValue(raw, "cooked_nutrition_per_day"); cooked {
		t.Fatal("cooked without a kitchen")
	}
	meal := vegetableMeal(1.5, 1000)
	cooked := CropChannels([]FoodField{field}, cookKitchen(true, 1, meal))[0]
	if n, _ := cooked.NutritionPerDay.Value(); n != 3 {
		t.Fatalf("cooked nutrition %v, want 3", n)
	}
	if w, _ := cooked.WorkPerDay.Value(); w != 100+3*1000 {
		t.Fatalf("cook labor missing from WorkPerDay: %v", w)
	}
	for name, k := range map[string]CropKitchen{"no usable bench": cookKitchen(false, 1, meal), "no cook": cookKitchen(true, 0, meal), "cook work over capacity": cookKitchen(true, 1, vegetableMeal(1.5, 10000))} {
		c := CropChannels([]FoodField{field}, k)[0]
		if n, _ := c.NutritionPerDay.Value(); n != 2 {
			t.Fatalf("%s: nutrition %v, want raw 2", name, n)
		}
		if w, _ := c.WorkPerDay.Value(); w != 100 {
			t.Fatalf("%s: work %v", name, w)
		}
	}
	lossy := CropChannels([]FoodField{field}, cookKitchen(true, 1, vegetableMeal(0.8, 1000)))[0]
	if n, _ := lossy.NutritionPerDay.Value(); n != 2 {
		t.Fatalf("a lossy meal beat raw: %v", n)
	}
}

func TestCropUnknownRecipeFactIsUnknownNotRaw(t *testing.T) {
	meal := vegetableMeal(1.5, 1000)
	meal.WorkPerNutrition = domain.Unknown[float64]()
	if _, known := cookKitchen(true, 1, meal).Cooking().Value(); known {
		t.Fatal("unknown work per nutrition read as a recipe")
	}
	c := CropChannels([]FoodField{cropField()}, cookKitchen(true, 1, meal))[0]
	if n, known := c.NutritionPerDay.Value(); !known || n != 2 {
		t.Fatalf("raw nutrition lost: %v %v", n, known)
	}
	if _, known := (CropKitchen{}).Cooking().Value(); known {
		t.Fatal("unknown kitchen read as cookable")
	}
	cooking, known := cookKitchen(false, 1, vegetableMeal(1.5, 1)).Cooking().Value()
	if !known || cooking.Recipe != "" {
		t.Fatalf("a kitchen with no usable bench is known to cook nothing: %+v %v", cooking, known)
	}
}

func TestCropRotDaysCapStock(t *testing.T) {
	field := cropField() // 2/day
	if _, known := CropChannels([]FoodField{field}, CropKitchen{})[0].StockCap.Value(); known {
		t.Fatal("cap from unknown rot facts")
	}
	field.Plan.Crop.RotDays, field.Plan.Crop.Perishable = domain.Known(4.0), domain.Known(true)
	c := CropChannels([]FoodField{field}, CropKitchen{})[0]
	if stock, known := c.StockCap.Value(); !known || stock != 8 {
		t.Fatalf("stock cap %v %v, want 8", stock, known)
	}
	if v, _ := termValue(c, "rot_days"); v != 4 {
		t.Fatal(v)
	}
	field.Plan.Crop.Perishable = domain.Known(false)
	if _, known := CropChannels([]FoodField{field}, CropKitchen{})[0].StockCap.Value(); known {
		t.Fatal("a non-perishable harvest was capped")
	}
	field.Plan.Crop.Perishable = domain.Known(true)
	field.Plan.Crop.RotDays = domain.Unknown[float64]()
	if _, known := CropChannels([]FoodField{field}, CropKitchen{})[0].StockCap.Value(); known {
		t.Fatal("unknown rot days read as a cap")
	}

	// The cap limits what the plan credits over its window.
	field.Plan.Crop.RotDays = domain.Known(1.0)
	longRunway := func() FoodPlanRequest {
		r := foodPlanRequest(CropChannels([]FoodField{field}, CropKitchen{})...)
		r.Demand.RunwayDays = domain.Known(30.0)
		return r
	}
	capped, err := SupplyFoodPlan(longRunway())
	if err != nil {
		t.Fatal(err)
	}
	field.Plan.Crop.Perishable = domain.Known(false)
	free, err := SupplyFoodPlan(longRunway())
	if err != nil {
		t.Fatal(err)
	}
	if capped.DeliveredPerDay >= free.DeliveredPerDay {
		t.Fatalf("rot cap did not limit credit: capped %v free %v", capped.DeliveredPerDay, free.DeliveredPerDay)
	}
}
