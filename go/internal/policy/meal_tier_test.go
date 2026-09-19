package policy

import (
	"math"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tierChannel(kind FoodChannelKind) FoodPlanEntry {
	return FoodPlanEntry{Channel: FoodChannel{ID: string(kind), Kind: kind, Open: domain.Known(true)}, Decision: FoodPlanHold, DeliveredPerDay: 1}
}

func tierRequest() MealTierRequest {
	return MealTierRequest{
		Plan:          domain.Known(FoodPlan{DemandPerDay: 3, DeliveredPerDay: 3, Portfolio: []FoodPlanEntry{tierChannel(FoodCrop), tierChannel(FoodAnimalProduct)}}),
		RawRunwayDays: domain.Known(8.0), MinDays: 2, TargetDays: 7,
		Cooks: domain.Known([]MealCook{{Pawn: "cook", Skill: 12}}), HighExpectations: domain.Known(false), Previous: MealSimple,
		Paste:       domain.Known(Infrastructure{Name: "NutrientPasteDispenser", Available: domain.Known(true), PowerW: domain.Known(200.0)}),
		Environment: domain.Known(ControlledEnvironment{Networks: []PowerHeadroom{{ID: "grid", GenerationW: domain.Known(1000.0), SolarW: domain.Known(0.0), WindW: domain.Known(0.0), ConsumptionW: domain.Known(700.0), ActiveSource: domain.Known(true)}}}),
	}
}

func tierRecipe(name string, mood, efficiency, work float64, floor int32, slots ...FoodIngredientSlot) ProductionRecipe {
	return ProductionRecipe{Name: name, Available: domain.Known(true), Mood: domain.Known(mood), NutrientEfficiency: domain.Known(efficiency), WorkPerNutrition: domain.Known(work), CookSkillFloor: domain.Known(floor), NeedsPower: domain.Known(false), IngredientClasses: domain.Known(slots), Products: []ProductionProduct{{Name: name, Nutrition: domain.Known(0.9), Edible: domain.Known(true)}}}
}

func tierBenches() domain.Fact[[]ProductionBench] {
	protein := FoodIngredientSlot{Alternatives: []FoodIngredientClass{IngredientMeat, IngredientAnimalProduct}}
	veg := FoodIngredientSlot{Alternatives: []FoodIngredientClass{IngredientVegetable}}
	return domain.Known([]ProductionBench{{ID: "stove", Usable: domain.Known(true), Token: domain.Known("token"), Recipes: []ProductionRecipe{
		tierRecipe("CookMealLavish", 12, 1, 800, 8, protein, veg),
		tierRecipe("CookMealFine", 5, 1.8, 500, 6, protein, veg),
		tierRecipe("CookMealSimple", 0, 1.8, 333, 0, FoodIngredientSlot{Alternatives: []FoodIngredientClass{IngredientAny}}),
	}}})
}

func TestMealTierTable(t *testing.T) {
	for _, tc := range []struct {
		name       string
		change     func(*MealTierRequest)
		want       MealTier
		wantRecipe string
	}{
		{"surplus and milk", func(*MealTierRequest) {}, MealFine, "CookMealFine"},
		{"at target", func(r *MealTierRequest) { r.RawRunwayDays = domain.Known(7.0) }, MealSimple, "CookMealSimple"},
		{"upgrade deadband", func(r *MealTierRequest) { r.RawRunwayDays = domain.Known(7.1) }, MealSimple, "CookMealSimple"},
		{"upgrade boundary", func(r *MealTierRequest) { r.RawRunwayDays = domain.Known(7.5) }, MealFine, "CookMealFine"},
		{"retain fine at target", func(r *MealTierRequest) { r.Previous = MealFine; r.RawRunwayDays = domain.Known(7.0) }, MealFine, "CookMealFine"},
		{"downgrade below target", func(r *MealTierRequest) { r.Previous = MealFine; r.RawRunwayDays = domain.Known(6.99) }, MealSimple, "CookMealSimple"},
		{"cook below floor", func(r *MealTierRequest) { r.Cooks = domain.Known([]MealCook{{Pawn: "cook", Skill: 5}}) }, MealSimple, "CookMealSimple"},
		{"cook at floor", func(r *MealTierRequest) { r.Cooks = domain.Known([]MealCook{{Pawn: "cook", Skill: 6}}) }, MealFine, "CookMealFine"},
		{"lavish pressure", func(r *MealTierRequest) { r.HighExpectations = domain.Known(true) }, MealLavish, "CookMealLavish"},
		{"lavish needs qualified cook", func(r *MealTierRequest) {
			r.HighExpectations = domain.Known(true)
			r.Cooks = domain.Known([]MealCook{{Pawn: "cook", Skill: 7}})
		}, MealFine, "CookMealFine"},
		{"lavish requires surplus", func(r *MealTierRequest) {
			r.HighExpectations = domain.Known(true)
			r.Previous = MealLavish
			r.RawRunwayDays = domain.Known(7.0)
		}, MealFine, "CookMealFine"},
		{"unknown pressure cannot keep lavish", func(r *MealTierRequest) { r.HighExpectations = domain.Unknown[bool](); r.Previous = MealLavish }, MealFine, "CookMealFine"},
		{"scarce raw chooses paste", func(r *MealTierRequest) { r.RawRunwayDays = domain.Known(1.0) }, MealPaste, ""},
		{"no allocated cook chooses paste", func(r *MealTierRequest) { r.Cooks = domain.Known([]MealCook{}) }, MealPaste, ""},
		{"cook labor constrained", func(r *MealTierRequest) { r.CookLaborConstrained = domain.Known(true) }, MealPaste, ""},
		{"paste holds within minimum deadband", func(r *MealTierRequest) { r.Previous = MealPaste; r.RawRunwayDays = domain.Known(2.2) }, MealPaste, ""},
		{"paste exits recovered minimum", func(r *MealTierRequest) { r.Previous = MealPaste; r.RawRunwayDays = domain.Known(2.5) }, MealSimple, "CookMealSimple"},
		{"unknown power falls back to simple", func(r *MealTierRequest) {
			r.RawRunwayDays = domain.Known(1.0)
			r.Environment = domain.Unknown[ControlledEnvironment]()
		}, MealSimple, "CookMealSimple"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := tierRequest()
			tc.change(&r)
			review, err := ReviewMealTier(r, tierBenches())
			if err != nil || review.Tier != tc.want {
				t.Fatal(review, err)
			}
			bill, ok := SelectProductionBill(CookFood, tierBenches(), domain.Known(int64(3)), domain.Known(100.0), domain.Unknown[float64](), 7, ProductionBillContext{Meals: &r})
			if tc.wantRecipe == "" {
				if ok || review.PasteNetwork != "grid" {
					t.Fatal(bill, ok, review)
				}
				return
			}
			if !ok || bill.Recipe != tc.wantRecipe || bill.Target != 9 {
				t.Fatal(bill, ok)
			}
			if !strings.Contains(review.Explain(), "raw_runway_days=") {
				t.Fatal(review.Explain())
			}
		})
	}
}

func TestMealIngredientChannelsAndSlots(t *testing.T) {
	for _, tc := range []struct {
		name    string
		entries []FoodPlanEntry
		want    string
	}{
		{"milk and vegetables", []FoodPlanEntry{tierChannel(FoodCrop), tierChannel(FoodAnimalProduct)}, "CookMealFine"},
		{"meat and vegetables", []FoodPlanEntry{tierChannel(FoodForage), tierChannel(FoodHunt)}, "CookMealFine"},
		{"corpse and vegetables", []FoodPlanEntry{tierChannel(FoodCrop), tierChannel(FoodCorpse)}, "CookMealFine"},
		{"no protein", []FoodPlanEntry{tierChannel(FoodCrop)}, "CookMealSimple"},
		{"no vegetables", []FoodPlanEntry{tierChannel(FoodAnimalProduct)}, "CookMealSimple"},
		{"no raw sources", []FoodPlanEntry{tierChannel(FoodCook), tierChannel(FoodReserve)}, ""},
		{"no channels", nil, ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := tierRequest()
			p, _ := r.Plan.Value()
			p.Portfolio = tc.entries
			r.Plan = domain.Known(p)
			bill, ok := SelectProductionBill(CookFood, tierBenches(), domain.Known(int64(3)), domain.Known(100.0), domain.Unknown[float64](), 7, ProductionBillContext{Meals: &r})
			if ok != (tc.want != "") || bill.Recipe != tc.want {
				t.Fatal(bill, ok)
			}
		})
	}
	for _, state := range []string{"proposed", "closed", "unknown-open", "zero-delivery", "unknown-channel"} {
		t.Run(state, func(t *testing.T) {
			r := tierRequest()
			p, _ := r.Plan.Value()
			switch state {
			case "proposed":
				p.Portfolio[1].Decision = FoodPlanOpen
				p.Portfolio[1].Channel.Open = domain.Known(false)
			case "closed":
				p.Portfolio[1].Decision = FoodPlanClose
			case "unknown-open":
				p.Portfolio[1].Channel.Open = domain.Unknown[bool]()
			case "zero-delivery":
				p.Portfolio[1].DeliveredPerDay = 0
			case "unknown-channel":
				p.Unknown = append(p.Unknown, p.Portfolio[1])
				p.Portfolio = p.Portfolio[:1]
			}
			r.Plan = domain.Known(p)
			review, err := ReviewMealTier(r, tierBenches())
			if err != nil || review.Tier != MealSimple {
				t.Fatal(review, err)
			}
		})
	}
}

func TestMealTierHysteresisAcrossReviews(t *testing.T) {
	r := tierRequest()
	for _, step := range []struct {
		days float64
		want MealTier
	}{{8, MealFine}, {7.1, MealFine}, {7, MealFine}, {6.9, MealSimple}, {7.1, MealSimple}, {7.49, MealSimple}, {7.5, MealFine}} {
		r.RawRunwayDays = domain.Known(step.days)
		review, err := ReviewMealTier(r, tierBenches())
		if err != nil || review.Tier != step.want {
			t.Fatal(step, review, err)
		}
		r.Previous = review.Tier
	}
}

func TestMealTierPowerRequiresOneFirmNetwork(t *testing.T) {
	for _, scenario := range []string{"exact", "insufficient", "split", "solar", "wind", "inactive", "unknown", "duplicate"} {
		t.Run(scenario, func(t *testing.T) {
			r := tierRequest()
			r.RawRunwayDays = domain.Known(1.0)
			env, _ := r.Environment.Value()
			switch scenario {
			case "exact":
				env.Networks[0].ConsumptionW = domain.Known(800.0)
			case "insufficient":
				env.Networks[0].ConsumptionW = domain.Known(801.0)
			case "split":
				env.Networks[0].ConsumptionW = domain.Known(850.0)
				other := env.Networks[0]
				other.ID = "other"
				env.Networks = append(env.Networks, other)
			case "solar":
				env.Networks[0].SolarW = domain.Known(200.0)
			case "wind":
				env.Networks[0].WindW = domain.Known(200.0)
			case "inactive":
				env.Networks[0].ActiveSource = domain.Known(false)
			case "unknown":
				env.Networks[0].ConsumptionW = domain.Unknown[float64]()
			case "duplicate":
				env.Networks = append(env.Networks, env.Networks[0])
			}
			r.Environment = domain.Known(env)
			review, err := ReviewMealTier(r, tierBenches())
			if err != nil || (review.Tier == MealPaste) != (scenario == "exact") {
				t.Fatal(review, err)
			}
		})
	}
}

func TestLavishTierDoesNotFlapAtTarget(t *testing.T) {
	r := tierRequest()
	r.HighExpectations = domain.Known(true)
	for _, step := range []struct {
		days float64
		want MealTier
	}{{8, MealLavish}, {7.1, MealLavish}, {7, MealFine}, {7.1, MealFine}, {7.5, MealLavish}} {
		r.RawRunwayDays = domain.Known(step.days)
		review, err := ReviewMealTier(r, tierBenches())
		if err != nil || review.Tier != step.want {
			t.Fatal(step, review, err)
		}
		r.Previous = review.Tier
	}
}

func TestMealTierInvalidAndUnknownInputs(t *testing.T) {
	for _, change := range []func(*MealTierRequest){
		func(r *MealTierRequest) { r.RawRunwayDays = domain.Unknown[float64]() },
		func(r *MealTierRequest) { r.RawRunwayDays = domain.Known(math.NaN()) },
		func(r *MealTierRequest) { r.RawRunwayDays = domain.Known(-1.0) },
		func(r *MealTierRequest) { r.MinDays = r.TargetDays },
		func(r *MealTierRequest) { r.Previous = "unknown" },
		func(r *MealTierRequest) { r.Plan = domain.Unknown[FoodPlan]() },
		func(r *MealTierRequest) { r.Cooks = domain.Unknown[[]MealCook]() },
		func(r *MealTierRequest) { r.Cooks = domain.Known([]MealCook{{Pawn: "cook", Skill: 21}}) },
		func(r *MealTierRequest) {
			r.Cooks = domain.Known([]MealCook{{Pawn: "cook", Skill: 6}, {Pawn: "cook", Skill: 6}})
		},
		func(r *MealTierRequest) {
			p, _ := r.Plan.Value()
			p.Portfolio[0].DeliveredPerDay = math.NaN()
			r.Plan = domain.Known(p)
		},
	} {
		r := tierRequest()
		change(&r)
		if _, err := ReviewMealTier(r, tierBenches()); err == nil {
			t.Fatal(r)
		}
	}
}

func TestMealRecipesFailClosedAndRankByCost(t *testing.T) {
	r := tierRequest()
	for _, change := range []func(*ProductionRecipe){
		func(p *ProductionRecipe) { p.IngredientClasses = domain.Unknown[[]FoodIngredientSlot]() },
		func(p *ProductionRecipe) { p.CookSkillFloor = domain.Unknown[int32]() },
		func(p *ProductionRecipe) { p.Mood = domain.Known(math.NaN()) },
		func(p *ProductionRecipe) { p.NutrientEfficiency = domain.Known(0.0) },
		func(p *ProductionRecipe) { p.WorkPerNutrition = domain.Known(-1.0) },
		func(p *ProductionRecipe) { p.NeedsPower = domain.Unknown[bool]() },
		func(p *ProductionRecipe) {
			p.IngredientClasses = domain.Known([]FoodIngredientSlot{{Alternatives: []FoodIngredientClass{IngredientAny, IngredientVegetable}}})
		},
		func(p *ProductionRecipe) {
			p.IngredientClasses = domain.Known([]FoodIngredientSlot{{Alternatives: []FoodIngredientClass{"unknown"}}})
		},
	} {
		rows, _ := tierBenches().Value()
		change(&rows[0].Recipes[1])
		review, err := ReviewMealTier(r, domain.Known(rows))
		if err != nil || review.Tier != MealSimple {
			t.Fatal(review, err)
		}
	}
	rows, _ := tierBenches().Value()
	bulk := rows[0].Recipes[1]
	bulk.Name = "mod-fine"
	bulk.WorkPerNutrition = domain.Known(100.0)
	rows[0].Recipes = append(rows[0].Recipes, bulk)
	before := append([]ProductionRecipe(nil), rows[0].Recipes...)
	bill, ok := SelectProductionBill(CookFood, domain.Known(rows), domain.Known(int64(2)), domain.Known(1.0), domain.Unknown[float64](), 7, ProductionBillContext{Meals: &r})
	if !ok || bill.Recipe != "mod-fine" {
		t.Fatal(bill, ok)
	}
	if !reflect.DeepEqual(before, rows[0].Recipes) {
		t.Fatal("selection mutated input recipes")
	}
	rows[0].Bills = []ExistingProductionBill{{Recipe: "CookMealFine"}}
	if _, ok := SelectProductionBill(CookFood, domain.Known(rows), domain.Known(int64(2)), domain.Known(1.0), domain.Unknown[float64](), 7, ProductionBillContext{Meals: &r}); ok {
		t.Fatal("matching existing bill was bypassed")
	}
}

func TestMealTierConsumesActualFoodPlan(t *testing.T) {
	r := tierRequest()
	channels := []FoodChannel{}
	for _, kind := range []FoodChannelKind{FoodCrop, FoodAnimalProduct} {
		channels = append(channels, FoodChannel{Kind: kind, ID: string(kind), Open: domain.Known(true), NutritionPerDay: domain.Known(1.5), WorkPerDay: domain.Known(50.0), LeadDays: domain.Known(0.0)})
	}
	p, err := PlanFood(FoodPlanRequest{Demand: FoodForecast{RunwayDays: domain.Known(8.0), Consumers: []ConsumerFoodForecast{{ID: "pawn", NutritionPerDay: 3}}}, MinDays: 2, TargetDays: 7, Channels: domain.Known(channels), Labor: domain.Known(1000.0)})
	if err != nil {
		t.Fatal(err)
	}
	r.Plan = domain.Known(p)
	review, err := ReviewMealTier(r, tierBenches())
	if err != nil || review.Tier != MealFine {
		t.Fatal(review, err, p)
	}
	// The ledger retains already-open sources as Hold. That is usable supply,
	// while an Open recommendation for an unstarted channel is not stock.
	for _, entry := range p.Portfolio {
		if entry.Decision != FoodPlanHold {
			t.Fatal(entry)
		}
	}
}

func TestMealBillContextDoesNotChangeOtherPurposes(t *testing.T) {
	r := tierRequest()
	for _, context := range []ProductionBillContext{{}, {Meals: &r}} {
		bill, ok := SelectProductionBill(CookFood, tierBenches(), domain.Known(int64(3)), domain.Known(8.0), domain.Unknown[float64](), 7, context)
		want := "CookMealSimple"
		if context.Meals != nil {
			want = "CookMealFine"
		}
		if !ok || bill.Recipe != want {
			t.Fatal(bill, ok)
		}
	}
	for _, purpose := range []BillPurpose{ButcherFood, CookAheadFood, PreserveFood} {
		if _, ok := SelectProductionBill(purpose, tierBenches(), domain.Known(int64(3)), domain.Known(8.0), domain.Known(5.0), 7, ProductionBillContext{Meals: &r}); ok {
			t.Fatal("meal context accepted for", purpose)
		}
	}
	if _, ok := SelectProductionBill(CookFood, tierBenches(), domain.Known(int64(3)), domain.Known(8.0), domain.Unknown[float64](), 6, ProductionBillContext{Meals: &r}); ok {
		t.Fatal("inconsistent seasonal target accepted")
	}
}
