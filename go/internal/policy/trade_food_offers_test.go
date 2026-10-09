package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func foodOffers(trader string, rows ...TradeOffer) TradeOffers {
	return TradeOffers{Trader: trader, Silver: 1000, GoodsStacks: 10, Rows: rows}
}

func foodRow(def string, count int64, price, nutrition float64) TradeOffer {
	return TradeOffer{Def: def, Count: count, Price: price, Food: domain.Known(TradeFoodGood{Nutrition: nutrition, Class: IngredientAny, Prepared: true})}
}

// A present trader's food is one one-shot candidate: no lead, no steady work,
// the silver as upfront labor, capped by what the trader holds, the silver
// above the reserve and the wanted nutrition, cheapest per nutrition first.
func TestTradeFoodChannelsPricedAndCapped(t *testing.T) {
	t.Parallel()
	o := []TradeOffers{foodOffers("caravan", foodRow("Pemmican", 10, 6, 2), foodRow("MealSimple", 100, 10, 0.9), TradeOffer{Def: "Steel", Count: 50, Price: 8})}
	one := func(silver, reserve int64, want float64) SupplyCandidate {
		got := TradeFoodChannels(o, silver, reserve, want)
		if len(got) != 1 {
			t.Fatalf("channels %v", got)
		}
		return got[0]
	}
	c := one(1000, 200, 1000)
	stock, _ := c.Nutrition().StockCap.Value()
	if c.Kind != CandidateTrade || c.ID != "caravan" || stock <= 20 {
		t.Fatalf("pemmican then meals expected: %+v", c)
	}
	if lead, _ := c.LeadDays.Value(); lead != 0 {
		t.Errorf("lead %v", lead)
	}
	if work, _ := c.LaborPerDay.Value(); work != 0 {
		t.Errorf("steady work %v", work)
	}
	if ticks, _ := c.UpfrontCost.LaborTicks.Value(); ticks > 800 || ticks <= 60 {
		t.Errorf("upfront %v outside the spendable silver", ticks)
	}
	if got, _ := one(1000, 200, 16).Nutrition().StockCap.Value(); got != 16 {
		t.Errorf("want cap: %d (cheapest rows, rounded up to whole units)", got)
	}
	if got, _ := one(260, 200, 1000).Nutrition().StockCap.Value(); got != 20 {
		t.Errorf("afford cap: %d", got)
	}
	if got := TradeFoodChannels(o, 200, 200, 1000); len(got) != 0 {
		t.Errorf("no silver above the reserve: %v", got)
	}
}

// An arrival nobody has priced is never planned for: no record, a record with
// no food give no candidate.
func TestTradeFoodChannelsIgnoreWindfalls(t *testing.T) {
	t.Parallel()
	if got := TradeFoodChannels(nil, 1000, 200, 100); len(got) != 0 {
		t.Errorf("unrecorded trader: %v", got)
	}
	if got := TradeFoodChannels([]TradeOffers{steelOffers(0, 8, 100)}, 1000, 200, 100); len(got) != 0 {
		t.Errorf("record without food: %v", got)
	}
}

// Through the supply plan the candidate opens only when the ranker needs it,
// and the nutrition planned for a trader is its opened entry's stock.
func TestSupplyFoodPlanOpensTradeCandidate(t *testing.T) {
	t.Parallel()
	trade := TradeFoodChannels([]TradeOffers{foodOffers("caravan", foodRow("Pemmican", 100, 6, 2))}, 1000, 200, 40)
	late := foodPlanChannel("late", 12, 100, 4, false)
	plan, err := SupplyFoodPlan(foodPlanRequest(append([]SupplyCandidate{late}, trade...)...))
	if err != nil {
		t.Fatal(err)
	}
	if got := PlannedTradeNutrition(domain.Known(plan), "caravan"); got != 40 {
		t.Fatalf("trade not opened: %s", plan.Explain())
	}
	if got := PlannedTradeNutrition(domain.Known(plan), "other"); got != 0 {
		t.Errorf("another trader's offer planned %v", got)
	}
	early := foodPlanChannel("hunt", 20, 10, 0, false)
	early.Kind = CandidateHunt
	plan, err = SupplyFoodPlan(foodPlanRequest(append([]SupplyCandidate{early}, trade...)...))
	if err != nil {
		t.Fatal(err)
	}
	if got := PlannedTradeNutrition(domain.Known(plan), "caravan"); got != 0 {
		t.Errorf("trade opened though a hunt covers the demand now: %s", plan.Explain())
	}
}
