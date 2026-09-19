package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tradeFoodContext() TradeFoodContext {
	return TradeFoodContext{Plan: domain.Known(FoodPlan{DemandPerDay: 10, GapPerDay: 12, Portfolio: []FoodPlanEntry{{Channel: foodPlanChannel("crop", 12, 100, 4, false), Decision: FoodPlanHold}}}), RunwayDays: domain.Known(1.0), MinDays: 3, TargetDays: 7}
}

func foodTradeNeed(r TradeFoodContext) TradeNeed {
	n, _ := ReviewTradeNeed(MedicalReserveReview{Replenish: domain.Known(int64(0))}, domain.Known([]Amount{}), nil, nil, domain.Unknown[WealthFacts](), RoutineTradePolicy{}, r).Value()
	return n
}

func TestRoutineTradeGoalConsumesSharedFoodPlan(t *testing.T) {
	f := stableRoutine()
	r := tradeFoodContext()
	f.FoodPlan = r.Plan
	f.FoodDays = r.RunwayDays
	f.Resources = domain.Known([]Amount{})
	f.Traders = domain.Known([]TraderFacts{{ID: "trader", CanTrade: true}})
	if got := needs(t, f, RoutineLatches{}); !hasNeed(got, TradeWithCaravan) {
		t.Fatal("food-only shortage did not activate trade", got)
	}
	p, _ := f.FoodPlan.Value()
	p.Portfolio[0].Channel.Kind = FoodHunt
	p.Portfolio[0].Channel.LeadDays = domain.Known(0.0)
	f.FoodPlan = domain.Known(p)
	if got := needs(t, f, RoutineLatches{}); hasNeed(got, TradeWithCaravan) {
		t.Fatal("immediate hunt did not suppress food trade", got)
	}
}

func TestTradeUsesObservedMealTierAndSubtractsProteinStock(t *testing.T) {
	slots := []FoodIngredientSlot{{Alternatives: []FoodIngredientClass{IngredientMeat, IngredientAnimalProduct}}, {Alternatives: []FoodIngredientClass{IngredientVegetable}}}
	benches := domain.Known([]ProductionBench{{ID: "stove", Usable: domain.Known(true), Bills: []ExistingProductionBill{{Recipe: "fine"}}, Recipes: []ProductionRecipe{{Name: "fine", Mood: domain.Known(5.0), IngredientClasses: domain.Known(slots)}}}})
	if got, known := TradeMealIngredients(benches).Value(); !known || len(got) != 2 {
		t.Fatal(got, known)
	}
	rows := []TradeSheetRowFact{foodTradeRow("meat", 4, 100, TradeFoodGood{Nutrition: 0.5, Class: IngredientMeat})}
	if targets := tradeFoodTargets(TradeFoodNeed{Missing: slots[:1], IngredientNutrition: 2}, rows); len(targets) != 0 {
		t.Fatal("stock already covers protein need", targets)
	}
}

func TestTradeFoodBridgeWaitsForExhaustion(t *testing.T) {
	for _, tc := range []struct {
		name   string
		change func(*TradeFoodContext)
		want   float64
	}{
		{"late channel", func(r *TradeFoodContext) {}, 48},
		{"at minimum", func(r *TradeFoodContext) { r.RunwayDays = domain.Known(3.0) }, 0},
		{"hunt now", func(r *TradeFoodContext) {
			p, _ := r.Plan.Value()
			c := foodPlanChannel("hunt", 10, 10, 0, false)
			c.Kind = FoodHunt
			p.Portfolio = append(p.Portfolio, FoodPlanEntry{Channel: c, Decision: FoodPlanOpen})
			r.Plan = domain.Known(p)
		}, 0},
		{"at exhaustion", func(r *TradeFoodContext) {
			p, _ := r.Plan.Value()
			p.Portfolio[0].Channel.LeadDays = domain.Known(1.0)
			r.Plan = domain.Known(p)
		}, 0},
		{"unknown lead", func(r *TradeFoodContext) {
			p, _ := r.Plan.Value()
			p.Portfolio[0].Channel.LeadDays = domain.Unknown[float64]()
			r.Plan = domain.Known(p)
		}, 0},
		{"unknown row", func(r *TradeFoodContext) {
			p, _ := r.Plan.Value()
			p.Unknown = []FoodPlanEntry{{}}
			r.Plan = domain.Known(p)
		}, 0},
		{"no channels", func(r *TradeFoodContext) { p, _ := r.Plan.Value(); p.Portfolio = nil; r.Plan = domain.Known(p) }, 84},
		{"unknown plan", func(r *TradeFoodContext) { r.Plan = domain.Unknown[FoodPlan]() }, 0},
		{"invalid runway", func(r *TradeFoodContext) { r.RunwayDays = domain.Known(math.NaN()) }, 0},
		{"no gap", func(r *TradeFoodContext) { p, _ := r.Plan.Value(); p.GapPerDay = 0; r.Plan = domain.Known(p) }, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := tradeFoodContext()
			tc.change(&r)
			n := foodTradeNeed(r)
			if n.Food.Nutrition != tc.want || n.Any() != (tc.want > 0) {
				t.Fatalf("got %+v want %v", n, tc.want)
			}
		})
	}
}

func foodTradeRow(id string, stock, supply int64, g TradeFoodGood) TradeSheetRowFact {
	r := tradeRow(id, id, stock, supply, 1, 1)
	r.Food = domain.Known(g)
	r.ProtectedExport = true
	return r
}

func TestTradeFoodTargetsPreferDurableThenMealsThenRaw(t *testing.T) {
	rows := []TradeSheetRowFact{
		foodTradeRow("raw", 0, 100, TradeFoodGood{Nutrition: 0.5, Class: IngredientVegetable, Crop: true}),
		foodTradeRow("meal", 0, 2, TradeFoodGood{Nutrition: 1, Class: IngredientAny, Prepared: true}),
		foodTradeRow("durable", 0, 2, TradeFoodGood{Nutrition: 1, Class: IngredientAny, Prepared: true, NonPerishable: true}),
	}
	p := RoutineTradeTargets(TradeNeed{Food: TradeFoodNeed{Nutrition: 5}}, rows, nil, RoutineTradePolicy{})
	if len(p.Targets) != 3 || p.Targets[0].Item != "durable" || p.Targets[1].Item != "meal" || p.Targets[2].MaxBuy != 2 {
		t.Fatal(p)
	}
	got := selectedCounts(t, SelectTrade(p, tradeFacts(rows, 100, 100, 100)))
	if got["durable"] != 2 || got["meal"] != 2 || got["raw"] != 2 {
		t.Fatal(got)
	}
	// A missing classification or ambiguous definition cannot consume the
	// nutrition budget and starve the known fallback of a target.
	rows[2].Food = domain.Unknown[TradeFoodGood]()
	p = RoutineTradeTargets(TradeNeed{Food: TradeFoodNeed{Nutrition: 5}}, rows, nil, RoutineTradePolicy{})
	if len(p.Targets) != 2 || p.Targets[1].MaxBuy != 6 {
		t.Fatal(p)
	}
}

func TestTradeFoodPrefersPemmicanDespiteItsRotClock(t *testing.T) {
	rows := []TradeSheetRowFact{
		foodTradeRow("MealSimple", 0, 100, TradeFoodGood{Nutrition: 0.9, Class: IngredientAny, Prepared: true}),
		foodTradeRow("Pemmican", 0, 100, TradeFoodGood{Nutrition: 0.05, Class: IngredientAny, Prepared: true}),
	}
	targets := tradeFoodTargets(TradeFoodNeed{Nutrition: 1}, rows)
	if len(targets) != 1 || targets[0].Item != "Pemmican" || targets[0].MaxBuy != 20 {
		t.Fatal(targets)
	}
}

func TestTradeFoodMissingProteinAndCropFloors(t *testing.T) {
	r := tradeFoodContext()
	r.RunwayDays = domain.Known(9.0)
	r.IngredientNutrition = 2
	r.DesiredIngredients = domain.Known([]FoodIngredientSlot{{Alternatives: []FoodIngredientClass{IngredientMeat, IngredientAnimalProduct}}, {Alternatives: []FoodIngredientClass{IngredientVegetable}}})
	p, _ := r.Plan.Value()
	p.Portfolio[0].Channel.Kind = FoodCrop
	p.Portfolio[0].Channel.Open = domain.Known(true)
	p.Portfolio[0].DeliveredPerDay = 12
	r.Plan = domain.Known(p)
	n, known := ReviewTradeNeed(MedicalReserveReview{Replenish: domain.Known(int64(0))}, domain.Known([]Amount{{Resource: "crop", Count: 100}}), map[Resource]int64{"crop": 60}, map[string]int64{"crop": 80}, domain.Unknown[WealthFacts](), RoutineTradePolicy{}, r).Value()
	if !known || len(n.Food.Missing) != 1 || n.Surplus[0].Count != 20 || n.Retained["crop"] != 80 {
		t.Fatal(n)
	}
	rows := []TradeSheetRowFact{foodTradeRow("meat", 0, 100, TradeFoodGood{Nutrition: 0.5, Class: IngredientMeat}), foodTradeRow("crop", 100, 0, TradeFoodGood{Nutrition: 0.5, Class: IngredientVegetable, Crop: true})}
	economic := RoutineTradeTargets(n, rows, map[Resource]int64{"crop": 60}, RoutineTradePolicy{})
	facts := tradeFacts(rows, 100, 100, 100)
	facts.CropSurplusFloors = CropSurplusFloors(n)
	got := selectedCounts(t, SelectTrade(economic, facts))
	if got["meat"] != 4 || got["crop"] != -20 {
		t.Fatal(got)
	}
	facts.Floors = map[string]int64{"crop": 95}
	got = selectedCounts(t, SelectTrade(economic, facts))
	if got["crop"] != -5 {
		t.Fatal(got)
	}
	for _, tc := range []struct {
		name   string
		change func(*TradeSelectionFacts)
	}{
		{"no authorization", func(f *TradeSelectionFacts) { f.CropSurplusFloors = nil }},
		{"no purchase budget", func(f *TradeSelectionFacts) { f.MaxSilverSpend = 0 }},
		{"unknown classification", func(f *TradeSelectionFacts) { f.Rows[1].Food = domain.Unknown[TradeFoodGood]() }},
		{"prepared food", func(f *TradeSelectionFacts) {
			f.Rows[1].Food = domain.Known(TradeFoodGood{Nutrition: 1, Class: IngredientVegetable, Prepared: true})
		}},
		{"unknown protection", func(f *TradeSelectionFacts) { f.Rows[1].ProtectedExportKnown = false }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			f := facts
			f.Rows = append([]TradeSheetRowFact(nil), facts.Rows...)
			tc.change(&f)
			if got := selectedCounts(t, SelectTrade(economic, f)); got["crop"] != 0 {
				t.Fatal(got)
			}
		})
	}
	p.Portfolio = append(p.Portfolio, FoodPlanEntry{Channel: FoodChannel{Kind: FoodAnimalProduct, ID: "milk", Open: domain.Known(true)}, Decision: FoodPlanHold, DeliveredPerDay: 1})
	r.Plan = domain.Known(p)
	if n := foodTradeNeed(r); len(n.Food.Missing) != 0 {
		t.Fatal("retained milk already supplies protein", n)
	}
}
