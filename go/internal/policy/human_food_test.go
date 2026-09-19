package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"reflect"
	"testing"
)

func TestHumanButcherGate(t *testing.T) {
	for _, trait := range []string{"", "Psychopath", "Bloodlust", "Cannibal", "Kind"} {
		for _, precept := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false), domain.Known(true)} {
			for _, work := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false), domain.Known(true)} {
				p, _ := precept.Value()
				w, _ := work.Value()
				want := w && (p || trait == "Psychopath" || trait == "Bloodlust" || trait == "Cannibal")
				if got := HumanButcherEligible(domain.Known([]PawnTrait{{Name: trait}}), precept, work); got != want {
					t.Fatalf("trait=%s precept=%v work=%v: got %v", trait, precept, work, got)
				}
			}
		}
	}
	if HumanButcherEligible(domain.Unknown[[]PawnTrait](), domain.Unknown[bool](), domain.Known(true)) {
		t.Fatal("unknown disposition admitted")
	}
}

func TestHumanBillCoexistsWithAnimalBill(t *testing.T) {
	b := ProductionBench{ID: "bench", Butcher: true, Usable: domain.Known(true), Token: domain.Known("token"), HumanButchers: []HumanButcherCandidate{{ID: "z", PreceptAcceptable: domain.Known(true), CanWork: domain.Known(true)}, {ID: "a", PreceptAcceptable: domain.Known(true), CanWork: domain.Known(true)}}, HumanCorpseNutrition: domain.Known(10.), Recipes: []ProductionRecipe{{Name: "ButcherCorpseFlesh", Available: domain.Known(true)}}, Bills: []ExistingProductionBill{{Recipe: "ButcherCorpseFlesh"}}}
	got, ok := SelectHumanButcher(domain.Known([]ProductionBench{b}))
	if !ok || got.Worker != "a" || got.Mode != domain.HumanButcherForever {
		t.Fatal(got, ok)
	}
	b.Bills = append(b.Bills, ExistingProductionBill{Recipe: "ButcherCorpseFlesh", Humanlike: true})
	if _, ok = SelectHumanButcher(domain.Known([]ProductionBench{b})); ok {
		t.Fatal("duplicate human bill")
	}
	b.Bills = nil
	b.HumanButchers = nil
	if _, ok = SelectHumanButcher(domain.Known([]ProductionBench{b})); ok {
		t.Fatal("unqualified butcher")
	}
}

func TestHumanMeatRouting(t *testing.T) {
	for _, tc := range []struct {
		name    string
		request HumanMeatRouting
		want    []HumanMeatAllocation
	}{
		{"herd first then survival and raw sale", HumanMeatRouting{Nutrition: 10, FeedShortfall: 4, VegetableNutrition: 2, CanMakeSurvivalMeals: true}, []HumanMeatAllocation{{HumanMeatFeed, 4}, {HumanMeatSurvivalTrade, 2}, {HumanMeatRawTrade, 4}}},
		{"eligible meals after feed", HumanMeatRouting{Nutrition: 10, FeedShortfall: 4, EligibleMealDemand: 5, VegetableNutrition: 10, CanMakeSurvivalMeals: true}, []HumanMeatAllocation{{HumanMeatFeed, 4}, {HumanMeatSurvivalTrade, 1}, {HumanMeatMeals, 5}}},
		{"no vegetables", HumanMeatRouting{Nutrition: 10, CanMakeSurvivalMeals: true}, []HumanMeatAllocation{{HumanMeatRawTrade, 10}}},
		{"no production", HumanMeatRouting{Nutrition: 10, VegetableNutrition: 10}, []HumanMeatAllocation{{HumanMeatRawTrade, 10}}},
		{"feed consumes all", HumanMeatRouting{Nutrition: 2, FeedShortfall: 4}, []HumanMeatAllocation{{HumanMeatFeed, 2}}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := RouteHumanMeat(tc.request)
			if err != nil || !reflect.DeepEqual(got, tc.want) {
				t.Fatalf("%v %v", got, err)
			}
		})
	}
	for _, bad := range []float64{-1, math.NaN(), math.Inf(1)} {
		if _, err := RouteHumanMeat(HumanMeatRouting{Nutrition: bad}); err == nil {
			t.Fatal("invalid nutrition admitted")
		}
	}
}

func TestHumanMeatForecastDisposition(t *testing.T) {
	for _, accept := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false), domain.Known(true)} {
		s := foodFixture()
		s.Stocks = []FoodStock{durableFood("human-meal", 9, "", "a", "b")}
		s.Stocks[0].IsHumanMeat = true
		s.Consumers[0].HumanMeatAcceptable = accept
		s.Consumers[1].HumanMeatAcceptable = domain.Known(true)
		f, err := ForecastFood(s, []PawnID{"a"})
		if err != nil {
			t.Fatal(err)
		}
		allowed, _ := accept.Value()
		want := 0.0
		if allowed {
			want = 6
		}
		if f.UsableNutrition != want {
			t.Fatalf("disposition %v: %v", accept, f)
		}
	}
	// An unacceptable private meal must not become usable inventory nutrition.
	s := foodFixture()
	s.Stocks = []FoodStock{durableFood("human-meal", 9, "a", "a")}
	s.Stocks[0].IsHumanMeat = true
	f, err := ForecastFood(s, nil)
	if err != nil || f.InventoryNutrition != 0 || f.UsableNutrition != 0 {
		t.Fatalf("%v %v", f, err)
	}
}

func TestHumanFoodLedgerAndCookingFilters(t *testing.T) {
	s := foodFixture()
	s.Consumers[0].HumanMeatAcceptable = domain.Known(false)
	s.Consumers[1].HumanMeatAcceptable = domain.Known(true)
	meat := durableFood("meat", 10, "", "b")
	meat.IsHumanMeat = true
	meat.RawMeat = true
	meat.DefName = "Meat_Human"
	rice := durableFood("rice", 2, "", "a")
	rice.Vegetable = true
	rice.DefName = "RawRice"
	s.Stocks = []FoodStock{meat, rice}
	kibble := durableFood("kibble", 2, "", "b")
	kibble.IsHumanMeat = true
	kibble.DefName = "Kibble"
	s.Stocks = append(s.Stocks, kibble)
	b := ProductionBench{HumanButchers: []HumanButcherCandidate{{ID: "a", Traits: domain.Known([]PawnTrait{{Name: "Psychopath"}}), CanWork: domain.Known(true)}}, Usable: domain.Known(true)}
	channel, ok := HumanFoodChannel([]ProductionBench{b}, s, []PawnID{"a"}, 3)
	if !ok {
		t.Fatal("qualified channel missing")
	}
	if n, _ := channel.NutritionPerDay.Value(); n != 0 {
		t.Fatal("finite corpse stock counted as a production rate")
	}
	plan := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: channel, Decision: FoodPlanHold, Terms: channel.Terms}}})
	if !HumanFoodPending(plan) || HumanRouteNutrition(plan, HumanMeatFeed) <= 0 {
		t.Fatal("herd allocation missing", channel)
	}
	if got := HumanCookingIngredients(s, nil, plan, HumanMeatFeed); !reflect.DeepEqual(got, []string{"Meat_Human", "RawRice"}) {
		t.Fatal(got)
	}
	mealPlan := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: FoodChannel{Kind: FoodCorpse, ID: "human-butchery"}, Decision: FoodPlanHold, Terms: []FoodPlanTerm{{Name: string(HumanMeatMeals), Value: 3}}}}})
	if HumanCookingIngredients(s, s.Consumers, mealPlan, HumanMeatMeals) != nil {
		t.Fatal("mixed diners received a human meal filter")
	}
	s.Consumers[0].HumanMeatAcceptable = domain.Known(true)
	if len(HumanCookingIngredients(s, s.Consumers, mealPlan, HumanMeatMeals)) != 2 {
		t.Fatal("eligible diners excluded")
	}
	b.HumanButchers = nil
	if _, ok := HumanFoodChannel([]ProductionBench{b}, s, []PawnID{"a"}, 3); ok {
		t.Fatal("channel opened without a butcher")
	}
	meat.Reserve = true
	s.Stocks = []FoodStock{meat}
	reserve, e := ReviewFoodReserve(s, nil, 3, 1, domain.Unknown[[]float64]())
	if e != nil {
		t.Fatal(e)
	}
	if len(reserve.Release) > 0 {
		t.Fatal("human trade stock released as an ordinary reserve")
	}
}
