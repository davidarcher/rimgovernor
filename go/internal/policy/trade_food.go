package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TradeFoodContext comes from the shared food review, not a second plan made
// by trade. DesiredIngredients describes the desired recipe's conjunctive
// slots; IngredientNutrition is the nutrition to buy for each missing slot.
// Unknown facts leave food trading disabled without suppressing other needs.
type TradeFoodContext struct {
	Plan                domain.Fact[FoodPlan]
	RunwayDays          domain.Fact[float64]
	MinDays, TargetDays float64
	DesiredIngredients  domain.Fact[[]FoodIngredientSlot]
	IngredientNutrition float64
}

type TradeFoodNeed struct {
	Nutrition           float64
	Missing             []FoodIngredientSlot
	IngredientNutrition float64
}

// TradeFoodGood is a native definition classification, including whether a
// vegetable is a raw crop eligible for surplus export. It must not be inferred
// from localized labels. Zero/unknown classification never authorizes a sale.
type TradeFoodGood struct {
	Nutrition                     float64
	Class                         FoodIngredientClass
	Prepared, NonPerishable, Crop bool
}

func reviewTradeFood(r TradeFoodContext) TradeFoodNeed {
	plan, pk := r.Plan.Value()
	runway, rk := r.RunwayDays.Value()
	if !pk || !rk || !foodNumber(runway) || !fieldPositive(r.MinDays) || !fieldPositive(r.TargetDays) || r.TargetDays <= r.MinDays {
		return TradeFoodNeed{}
	}
	sources, _, valid := mealIngredientSources(plan)
	if !valid {
		return TradeFoodNeed{}
	}
	need := TradeFoodNeed{}
	if runway < r.MinDays && plan.GapPerDay > 0 && len(plan.Unknown) == 0 {
		earliest := math.Inf(1)
		for _, entry := range plan.Portfolio {
			if entry.Decision == FoodPlanClose || entry.Channel.Kind == FoodTrade || entry.Channel.Kind == FoodReserve || entry.Channel.Kind == FoodCook {
				continue
			}
			lead, known := entry.Channel.LeadDays.Value()
			nutrition, nk := entry.Channel.NutritionPerDay.Value()
			if !known || !nk || !foodNumber(lead) || !foodNumber(nutrition) {
				return TradeFoodNeed{}
			}
			if nutrition > 0 {
				earliest = math.Min(earliest, lead)
			}
		}
		if earliest > runway {
			// No future producer has a finite arrival: bridge one target window
			// instead of creating an unbounded purchase.
			if math.IsInf(earliest, 1) {
				earliest = r.TargetDays
			}
			nutrition := plan.GapPerDay * earliest
			if fieldPositive(nutrition) {
				need.Nutrition = nutrition
			}
		}
	}
	// Ingredient upgrades cannot sell away an emergency reserve.
	if runway <= r.TargetDays || !fieldPositive(r.IngredientNutrition) {
		return need
	}
	slots, known := r.DesiredIngredients.Value()
	all := map[FoodIngredientClass]bool{IngredientMeat: true, IngredientVegetable: true, IngredientAnimalProduct: true}
	if !known || !mealSlotsSupported(slots, all) {
		return need
	}
	for _, slot := range slots {
		if mealSlotsSupported([]FoodIngredientSlot{slot}, sources) {
			continue
		}
		protein := FoodIngredientSlot{}
		for _, class := range slot.Alternatives {
			if class == IngredientMeat || class == IngredientAnimalProduct {
				protein.Alternatives = append(protein.Alternatives, class)
			}
		}
		if len(protein.Alternatives) > 0 {
			need.Missing = append(need.Missing, protein)
		}
	}
	if len(need.Missing) > 0 {
		need.IngredientNutrition = r.IngredientNutrition
	}
	return need
}

func validTradeFood(g TradeFoodGood) bool {
	return fieldPositive(g.Nutrition) && (g.Class == IngredientAny || g.Class == IngredientMeat || g.Class == IngredientVegetable || g.Class == IngredientAnimalProduct) && (!g.Crop || !g.Prepared && g.Class == IngredientVegetable)
}

// Food purchases share a nutrition budget across definitions; supply from a
// higher ranked definition reduces what the next may buy. Silver remains the
// selector's independently enforced budget.
func tradeFoodTargets(need TradeFoodNeed, rows []TradeSheetRowFact) []domain.TradeTarget {
	var candidates []TradeSheetRowFact
	counts := map[string]int{}
	for _, row := range rows {
		counts[row.DefName]++
	}
	for _, row := range rows {
		g, known := row.Food.Value()
		if !known || !validTradeFood(g) || counts[row.DefName] != 1 || row.ColonyCount < 0 || row.TraderCount <= 0 || !row.TraderWillTradeKnown || !row.TraderWillTrade || !row.PawnKnown || row.Pawn || !row.CurrencyKnown || row.Currency || !row.BuyPriceKnown || !fieldPositive(row.BuyPrice) || row.BuyPrice > tradeBuyPriceCeiling {
			continue
		}
		candidates = append(candidates, row)
	}
	rank := func(row TradeSheetRowFact, g TradeFoodGood) int {
		// Pemmican has a long native rot clock; reserve policy still prefers
		// it over short-lived meals without claiming that it cannot rot.
		if g.NonPerishable || ReserveFoodDefinition(Resource(row.DefName)) {
			return 0
		}
		if g.Prepared {
			return 1
		}
		return 2
	}
	sort.Slice(candidates, func(i, j int) bool {
		a, b := candidates[i], candidates[j]
		ga, _ := a.Food.Value()
		gb, _ := b.Food.Value()
		if rank(a, ga) != rank(b, gb) {
			return rank(a, ga) < rank(b, gb)
		}
		if a.BuyPrice/ga.Nutrition != b.BuyPrice/gb.Nutrition {
			return a.BuyPrice/ga.Nutrition < b.BuyPrice/gb.Nutrition
		}
		return a.DefName < b.DefName
	})
	var out []domain.TradeTarget
	used := map[string]int64{}
	buy := func(nutrition float64, slot *FoodIngredientSlot) {
		if !fieldPositive(nutrition) {
			return
		}
		for _, row := range candidates {
			g, _ := row.Food.Value()
			if slot != nil && (g.Prepared || !mealSlotsSupported([]FoodIngredientSlot{*slot}, map[FoodIngredientClass]bool{g.Class: true})) {
				continue
			}
			count := min(row.TraderCount-used[row.DefName], tradeRoutineMaximumCount-row.ColonyCount-used[row.DefName])
			if count <= 0 {
				continue
			}
			wanted := math.Ceil(nutrition / g.Nutrition)
			if wanted < float64(count) {
				count = int64(wanted)
			}
			used[row.DefName] += count
			nutrition -= float64(count) * g.Nutrition
			if nutrition <= 0 {
				break
			}
		}
	}
	buy(need.Nutrition, nil)
	for i := range need.Missing {
		buy(need.IngredientNutrition, &need.Missing[i])
	}
	for _, row := range candidates {
		if count := used[row.DefName]; count > 0 && len(out) < tradeRoutineMaximumTargets {
			out = append(out, domain.TradeTarget{Item: row.DefName, Stock: row.ColonyCount + count, MaxBuy: count, MaxBuyPrice: tradeBuyPriceCeiling})
		}
	}
	return out
}

// CropSurplusFloors authorizes only raw vegetable rows above an explicit
// retained target while buying a missing protein ingredient. SelectTrade still
// applies the economic floor and the export quantity/price/cash limits.
func CropSurplusFloors(need TradeNeed) map[string]int64 {
	if len(need.Food.Missing) == 0 || !fieldPositive(need.Food.IngredientNutrition) {
		return nil
	}
	out := map[string]int64{}
	for _, row := range need.Surplus {
		if floor, known := need.Retained[row.Resource]; known && floor > 0 && row.Count > 0 {
			out[string(row.Resource)] = floor
		}
	}
	return out
}

func selectedProteinPurchase(selected []TradeSelectionLine, rows []TradeSheetRowFact) bool {
	for _, line := range selected {
		if line.Count <= 0 {
			continue
		}
		for _, row := range rows {
			if row.LineID != line.LineID {
				continue
			}
			g, known := row.Food.Value()
			if known && validTradeFood(g) && !g.Prepared && (g.Class == IngredientMeat || g.Class == IngredientAnimalProduct) {
				return true
			}
		}
	}
	return false
}
