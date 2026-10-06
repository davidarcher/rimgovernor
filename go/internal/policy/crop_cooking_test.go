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
		RemainingGrowDays: domain.Known(2.0), WorkPerDay: domain.Known(100.0)}
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

func TestNewFieldLeadCrossesFrost(t *testing.T) {
	t.Parallel()
	grow := domain.Known(10.0)
	warm := NewFieldLeadDays(grow, domain.Known(Calendar{GrowingDays: 60, GrowingDaysRemaining: 60, Sowing: true}))
	frost := NewFieldLeadDays(grow, domain.Known(Calendar{GrowingDays: 60, GrowingDaysRemaining: 4, NonGrowingDays: 30, Sowing: true}))
	w, wk := warm.Value()
	f, fk := frost.Value()
	if !wk || !fk || w != 10 || f != 40 {
		t.Fatalf("warm %v frost %v", warm, frost)
	}
	if _, known := NewFieldLeadDays(grow, domain.Unknown[Calendar]()).Value(); known {
		t.Fatal("unknown calendar gave a lead")
	}
}

func TestNewFieldChannelsAreClosedCandidates(t *testing.T) {
	t.Parallel()
	crop := CropChoice{Name: "Plant_Rice", Available: domain.Known(true), Edible: domain.Known(true), DietAllowed: domain.Known(true), GrowDays: domain.Known(5.0),
		HarvestNutrition: domain.Known(1.0), Demand: domain.Known(2.0), HarvestWork: domain.Known(100.0), RotDays: domain.Known(10.0), Perishable: domain.Known(true)}
	r := FieldRequest{Choices: []CropChoice{crop}, Colonists: domain.Known(int64(3)), ReserveDays: 10, Coverage: domain.Known(0.0),
		Climate:  CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(60.0), OutdoorsDark: domain.Known(false)},
		Calendar: domain.Known(Calendar{GrowingDays: 60, GrowingDaysRemaining: 60, Sowing: true})}
	out := NewFieldChannels(r, CropKitchen{})
	if len(out) != 1 {
		t.Fatalf("candidates %v", out)
	}
	c := out[0]
	if s, _ := c.State().Value(); s != CandidateClosed || c.ID != NewFieldPrefix+"Plant_Rice" || c.Source != "" {
		t.Fatalf("state %v id %q", s, c.ID)
	}
	if up, _ := c.UpfrontTicks.Value(); up <= 0 {
		t.Fatal("no upfront sowing cost")
	}
	if lead, _ := c.LeadDays.Value(); lead != 5 {
		t.Fatalf("lead %v", lead)
	}
	if _, capKnown := c.StockCap.Value(); !capKnown {
		t.Fatal("rot cap missing")
	}
	if got := SupplyCandidateOfFood(c).UpfrontCost.LaborTicks; got != c.UpfrontTicks {
		t.Fatal("adapter dropped the upfront cost")
	}
	r.Climate.Sowing = domain.Known(false)
	if len(NewFieldChannels(r, CropKitchen{})) != 0 {
		t.Fatal("candidate offered while crops cannot be sown")
	}
}

// A planted field with no delivery behind it is designated, never credited as
// delivering; the ledger moves it.
func TestPlantedFieldIsDesignatedNotDelivering(t *testing.T) {
	t.Parallel()
	c := CropChannels([]FoodField{cropField()}, CropKitchen{})[0]
	if s, _ := c.State().Value(); s != CandidateDesignated {
		t.Fatalf("state %v", s)
	}
}
