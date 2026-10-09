package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestTradeCurrencyIsTheSheetsCurrencyRow: the coin is whatever the
// sheet flags as its currency, and a sheet with none flagged (or the flag
// unread) has no coin, so no selection is made against a guessed "Silver".
func TestTradeCurrencyIsTheSheetsCurrencyRow(t *testing.T) {
	coin := tradeRow("#c", "Credits", 0, 0, 1, 1)
	coin.Currency = true
	if def, ok := TradeCurrency([]TradeSheetRowFact{tradeRow("#0", "Steel", 1, 1, 1, 1), coin}); !ok || def != "Credits" {
		t.Fatalf("currency %q %v", def, ok)
	}
	coin.CurrencyKnown = false
	if def, ok := TradeCurrency([]TradeSheetRowFact{coin}); ok {
		t.Fatalf("unread currency flag gave %q", def)
	}
	if _, ok := TradeCurrency([]TradeSheetRowFact{tradeRow("#0", "Silver", 1, 1, 1, 1)}); ok {
		t.Fatal("a row named Silver is not the currency unless the sheet says so")
	}
	sheet := withCurrency([]TradeSheetRowFact{tradeRow("#0", "Steel", 1, 1, 1, 1)})
	sheet[len(sheet)-1].DefName = "Credits"
	selection := SelectTrade(domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 5, 5, 0, 10, 0)}}, TradeSelectionFacts{Complete: true, Rows: sheet, ColonySilver: 100, TraderSilver: 100, SilverKnown: true, MaxSilverSpend: 100})
	if selection.Refused {
		t.Fatalf("a sheet in another currency refused: %s", selection.Reason)
	}
	if selection = SelectTrade(domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 5, 5, 0, 10, 0)}}, TradeSelectionFacts{Complete: true, Rows: sheet[:1], ColonySilver: 100, TraderSilver: 100, SilverKnown: true, MaxSilverSpend: 100}); !selection.Refused {
		t.Fatal("a sheet with no currency row selected")
	}
}

// TestObservedMealIsWhatTheActiveBillCooks: the meal and its
// nutrition come from the recipe row of an active bill, the best mood
// first; nothing cooked is no meal.
func TestObservedMealIsWhatTheActiveBillCooks(t *testing.T) {
	recipe := func(name, product string, nutrition, mood float64) ProductionRecipe {
		return ProductionRecipe{Name: name, Mood: domain.Known(mood), Products: []ProductionProduct{{Name: product, Nutrition: domain.Known(nutrition), Edible: domain.Known(true)}}}
	}
	bench := ProductionBench{
		Recipes: []ProductionRecipe{recipe("CookSimple", "MealSimple", 0.9, 0), recipe("CookFine", "MealFine", 0.9, 5), recipe("CookLavish", "MealLavish", 0.95, 12)},
		Bills:   []ExistingProductionBill{{Recipe: "CookSimple", Active: domain.Known(true)}, {Recipe: "CookFine", Active: domain.Known(true)}, {Recipe: "CookLavish", Active: domain.Known(false)}},
	}
	if def, nutrition, ok := ObservedMeal([]ProductionBench{bench}); !ok || def != "MealFine" || nutrition != 0.9 {
		t.Fatalf("meal %q %v %v", def, nutrition, ok)
	}
	if _, _, ok := ObservedMeal(nil); ok {
		t.Fatal("no bench cooked a meal")
	}
}
