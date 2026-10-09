package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestReviewTradeNeedMeasuresShortfallsAndSurplus(t *testing.T) {
	medicine := MedicalReserveReview{Active: true, Replenish: domain.Known(int64(4))}
	resources := domain.Known([]Amount{{Resource: "Steel", Count: 500}, {Resource: ComponentResource, Count: 2}, {Resource: "WoodLog", Count: 40}})
	targets := map[Resource]int64{"Steel": 200, "WoodLog": 100, ComponentResource: 10}
	need, known := ReviewTradeNeed(CoreItemFacts(), medicine, resources, targets, nil, domain.Unknown[WealthFacts](), noRetained).Value()
	if !known || need.MedicineReplenish != 4 || need.ComponentShortfall != 8 || len(need.Surplus) != 1 || need.Surplus[0] != (Amount{Resource: "Steel", Count: 300}) {
		t.Fatal(need, known)
	}
	if _, known = ReviewTradeNeed(CoreItemFacts(), MedicalReserveReview{}, resources, targets, nil, domain.Unknown[WealthFacts](), noRetained).Value(); known {
		t.Fatal("unknown medicine reserve became a trade need")
	}
	if need, known = ReviewTradeNeed(CoreItemFacts(), MedicalReserveReview{Replenish: domain.Known(int64(0))}, resources, nil, nil, domain.Unknown[WealthFacts](), noRetained).Value(); !known || need.Any() {
		t.Fatal(need, known)
	}
}

// noRetained is a known consumption with nothing protected.
var noRetained = domain.Known(map[Resource]int64{})

// steelConsumed is a consumption ring that spends 100 steel a day, so the
// runway protects 500 over the 5-day horizon.
func steelConsumed(construction map[Resource]int64) domain.Fact[map[Resource]int64] {
	runway := ResourceRunway{Resource: "Steel", ConsumptionPerDay: domain.Known(100.0)}
	return TradeRetained(domain.Known(ResourceConsumption{WindowDays: 15}), []ResourceRunway{runway}, construction)
}

func TestWealthSurplusSellsHoardsDownToTheFloor(t *testing.T) {
	items := CoreItemFacts()
	stock := []Amount{{Resource: "Steel", Count: 2000}, {Resource: "Plasteel", Count: 100}, {Resource: "Gold", Count: 80}, {Resource: "Silver", Count: 5000}, {Resource: ComponentResource, Count: 90}}
	heavy := domain.Known(WealthFacts{Items: 30000, Buildings: 10000, Pawns: 8000, Total: 48000})
	retained := steelConsumed(nil)
	if got := WealthSurplus(stock, items, nil, nil, retained, domain.Unknown[WealthFacts]()); got != nil {
		t.Fatal("unknown wealth produced a surplus", got)
	}
	light := domain.Known(WealthFacts{Items: 10000, Buildings: 30000, Pawns: 8000, Total: 48000})
	if got := WealthSurplus(stock, items, nil, nil, retained, light); got != nil {
		t.Fatal("an item share under the threshold produced a surplus", got)
	}
	if got := WealthSurplus(stock, items, nil, nil, TradeRetained(domain.Unknown[ResourceConsumption](), nil, nil), heavy); got != nil {
		t.Fatal("unknown consumption produced a surplus", got)
	}
	// Steel is consumed and keeps its protected line (500); plasteel and gold
	// have no consumption and are wholly surplus; silver and components are
	// not deep deposits.
	want := []Amount{{Resource: "Gold", Count: 80}, {Resource: "Plasteel", Count: 100}, {Resource: "Steel", Count: 1500}}
	if got := WealthSurplus(stock, items, nil, nil, retained, heavy); !equalAmounts(got, want) {
		t.Fatal(got, want)
	}
	// Construction demand adds to the protected line.
	if got := WealthSurplus(stock, items, nil, nil, steelConsumed(map[Resource]int64{"Steel": 300, "Gold": 30}), heavy); !equalAmounts(got, []Amount{{Resource: "Gold", Count: 50}, {Resource: "Plasteel", Count: 100}, {Resource: "Steel", Count: 1200}}) {
		t.Fatal(got)
	}
	// An economic floor above the line raises the keep; a floor at stock
	// yields nothing.
	floors, _ := EconomicReserves(domain.TradeEconomicPolicy{}, TradeReserveFacts{Reserves: map[string]int64{"Steel": 800}})
	if got := WealthSurplus(stock, items, nil, floors, retained, heavy); !equalAmounts(got, []Amount{{Resource: "Gold", Count: 80}, {Resource: "Plasteel", Count: 100}, {Resource: "Steel", Count: 1200}}) {
		t.Fatal(got)
	}
	floors, _ = EconomicReserves(domain.TradeEconomicPolicy{}, TradeReserveFacts{Reserves: map[string]int64{"Steel": 2000, "Gold": 80, "Plasteel": 100}})
	if got := WealthSurplus(stock, items, nil, floors, retained, heavy); got != nil {
		t.Fatal("stock at the floor produced a surplus", got)
	}
	// A MaintainResource target above the line is a floor too.
	if got := WealthSurplus(stock, items, map[Resource]int64{"Steel": 1900}, nil, retained, heavy); !equalAmounts(got, []Amount{{Resource: "Gold", Count: 80}, {Resource: "Plasteel", Count: 100}, {Resource: "Steel", Count: 100}}) {
		t.Fatal(got)
	}
	if got := WealthSurplus(stock, items, nil, nil, retained, domain.Known(WealthFacts{Items: 100, Total: 0})); got != nil {
		t.Fatal("zero total wealth produced a surplus", got)
	}
}

func TestTradeSilverReserveFollowsColonistsAndFailsClosed(t *testing.T) {
	for _, tt := range []struct {
		colonists domain.Fact[int64]
		want      int64
		buy       bool
	}{{domain.Known(int64(1)), 200, true}, {domain.Known(int64(5)), 500, true}, {domain.Unknown[int64](), 200, false}, {domain.Known(int64(0)), 200, false}} {
		if got, buy := TradeSilverReserve(tt.colonists); got != tt.want || buy != tt.buy {
			t.Fatal(tt, got, buy)
		}
	}
	p := RoundsTradeTargets(CoreItemFacts(), TradeNeed{ComponentShortfall: 5}, nil, map[Resource]int64{ComponentResource: 10}, domain.Unknown[int64]())
	for _, target := range p.Targets {
		if target.MaxBuy != 0 {
			t.Fatal("unknown colonists bought", p.Targets)
		}
	}
}

func TestReviewTradeNeedMergesWealthSurplusBehindTargets(t *testing.T) {
	medicine := MedicalReserveReview{Replenish: domain.Known(int64(0))}
	resources := domain.Known([]Amount{{Resource: "Steel", Count: 2000}, {Resource: "Gold", Count: 80}})
	heavy := domain.Known(WealthFacts{Items: 30000, Buildings: 10000, Pawns: 8000, Total: 48000})
	retained := steelConsumed(nil)
	// The steel target wins over its protected line; gold has no consumption
	// and is wholly surplus, retained at nothing.
	need, known := ReviewTradeNeed(CoreItemFacts(), medicine, resources, map[Resource]int64{"Steel": 1000}, nil, heavy, retained).Value()
	if !known || !equalAmounts(need.Surplus, []Amount{{Resource: "Steel", Count: 1000}, {Resource: "Gold", Count: 80}}) || need.Retained["Steel"] != 1000 || need.Retained["Gold"] != 0 {
		t.Fatal(need, known)
	}
	// Without a target steel is retained at its protected line.
	if need, _ = ReviewTradeNeed(CoreItemFacts(), medicine, resources, nil, nil, heavy, retained).Value(); need.Retained["Steel"] != 500 {
		t.Fatal(need)
	}
	if need, known = ReviewTradeNeed(CoreItemFacts(), medicine, resources, nil, nil, domain.Unknown[WealthFacts](), retained).Value(); !known || need.Any() {
		t.Fatal("unknown wealth should leave only the target-driven surplus", need, known)
	}
	rows := []TradeSheetRowFact{{LineID: "l1", DefName: "Gold", ColonyCount: 80, SellPrice: 30, SellPriceKnown: true}}
	economic := RoundsTradeTargets(CoreItemFacts(), TradeNeed{Surplus: []Amount{{Resource: "Gold", Count: 30}}, Retained: map[Resource]int64{"Gold": 50}}, rows, nil, domain.Known(int64(3)))
	if len(economic.Targets) != 1 || economic.Targets[0].Stock != 50 || economic.Targets[0].MaxSell != 30 {
		t.Fatal("the wealth surplus sale should retain its floor as the target stock", economic.Targets)
	}
}

func equalAmounts(a, b []Amount) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func TestTradeRecoveredNeedsBothCaravanAndNeed(t *testing.T) {
	need := domain.Known(TradeNeed{MedicineReplenish: 2})
	none := domain.Known(TradeNeed{})
	caravan := domain.Known([]TraderFacts{{ID: "Trader_1", CanTrade: true}})
	dismissed := domain.Known([]TraderFacts{{ID: "Trader_1", CanTrade: false}})
	if v, known := TradeRecovered(caravan, need).Value(); !known || v {
		t.Fatal("caravan with a need should be a deficit")
	}
	if v, known := TradeRecovered(caravan, none).Value(); !known || !v {
		t.Fatal("caravan with nothing to trade should be recovered")
	}
	unpriced := domain.Known([]TraderFacts{{ID: "Trader_1", CanTrade: true, Unpriced: true}})
	if v, known := TradeRecovered(unpriced, none).Value(); !known || v {
		t.Fatal("an unpriced caravan should be browsed even with nothing to trade")
	}
	if v, known := TradeRecovered(domain.Known([]TraderFacts{{ID: "Trader_1", Unpriced: true}}), none).Value(); !known || !v {
		t.Fatal("an untradeable caravan is never browsed")
	}
	if v, known := TradeRecovered(dismissed, need).Value(); !known || !v {
		t.Fatal("untradeable caravan should be recovered")
	}
	arriving := domain.Known([]TraderFacts{{ID: "Trader_1", Travelling: true}})
	if v, known := TradeRecovered(arriving, need).Value(); !known || v {
		t.Fatal("an arriving caravan with a need should hold the goal open")
	}
	if v, known := TradeRecovered(domain.Unknown[[]TraderFacts](), need).Value(); !known || !v {
		t.Fatal("unread census should leave the goal off")
	}
	if _, known := TradeRecovered(caravan, domain.Unknown[TradeNeed]()).Value(); known {
		t.Fatal("unknown need with a caravan present is unknown")
	}
}

func TestSelectTraderSkipsSettledAndPrefersGoods(t *testing.T) {
	traders := []TraderFacts{{ID: "b", CanTrade: true, GoodsStacks: 5}, {ID: "a", CanTrade: true, GoodsStacks: 5}, {ID: "c", CanTrade: true, GoodsStacks: 9}, {ID: "d", CanTrade: false, GoodsStacks: 20}}
	if pick, ok := SelectTrader(traders, nil); !ok || pick.ID != "c" {
		t.Fatal(pick, ok)
	}
	if pick, ok := SelectTrader(traders, map[string]bool{"c": true}); !ok || pick.ID != "a" {
		t.Fatal(pick, ok)
	}
	if _, ok := SelectTrader(traders, map[string]bool{"a": true, "b": true, "c": true}); ok {
		t.Fatal("every tradeable caravan settled")
	}
}

func TestRoundsTradeTargetsBuyCheapestMedicineThenSellSurplus(t *testing.T) {
	rows := []TradeSheetRowFact{
		{LineID: "#0", DefName: "MedicineIndustrial", ColonyCount: 1, TraderCount: 10, BuyPrice: 18, BuyPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true},
		{LineID: "#1", DefName: "MedicineHerbal", ColonyCount: 0, TraderCount: 10, BuyPrice: 9, BuyPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true},
		{LineID: "#2", DefName: "Steel", ColonyCount: 500, TraderCount: 0, SellPrice: 1.2, SellPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true},
		{LineID: "#3", DefName: "Silver", ColonyCount: 100, TraderCount: 900, Currency: true, CurrencyKnown: true, PawnKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true},
	}
	need := TradeNeed{MedicineReplenish: 4, Surplus: []Amount{{Resource: "Steel", Count: 300}}}
	p := RoundsTradeTargets(CoreItemFacts(), need, rows, map[Resource]int64{"Steel": 200}, domain.Known(int64(2)))
	if err := p.Validate(); err != nil {
		t.Fatal(err)
	}
	if len(p.Targets) != 2 || p.Targets[0].Item != "MedicineHerbal" || p.Targets[0].MaxBuy != 4 || p.Targets[0].Stock != 4 || p.Targets[1].Item != "Steel" || p.Targets[1].MaxSell != 300 || p.Targets[1].Stock != 200 {
		t.Fatal(p.Targets)
	}
	selection := SelectTrade(p, TradeSelectionFacts{Complete: true, Rows: withCurrency(rows), ColonySilver: 300, TraderSilver: 900, SilverKnown: true, MaxSilverSpend: 80})
	if selection.Refused || len(selection.Selected) != 2 || selection.Selected[0] != (TradeSelectionLine{LineID: "#1", DefName: "MedicineHerbal", Count: 4}) || selection.Selected[1] != (TradeSelectionLine{LineID: "#2", DefName: "Steel", Count: -300}) {
		t.Fatal(selection)
	}
	if p = RoundsTradeTargets(CoreItemFacts(), TradeNeed{MedicineReplenish: 4}, rows[2:], nil, domain.Known(int64(3))); len(p.Targets) != 0 {
		t.Fatal("trader without medicine produced a target", p.Targets)
	}
}

// A MaintainResource floor short of stock is a trade need and a buy
// target up to the floor.
func TestTradeBuysResourceShortfall(t *testing.T) {
	need, _ := ReviewTradeNeed(CoreItemFacts(), MedicalReserveReview{Replenish: domain.Known(int64(0))}, domain.Known([]Amount{{Resource: "WoodLog", Count: 50}}), map[Resource]int64{"WoodLog": 200}, nil, domain.Unknown[WealthFacts](), noRetained).Value()
	if !need.Any() || len(need.Shortfall) != 1 || need.Shortfall[0] != (Amount{Resource: "WoodLog", Count: 150}) {
		t.Fatalf("%+v", need)
	}
	rows := []TradeSheetRowFact{
		{LineID: "#0", DefName: "WoodLog", ColonyCount: 50, TraderCount: 400, BuyPrice: 1.5, BuyPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true},
		{LineID: "#1", DefName: "Silver", ColonyCount: 500, TraderCount: 900, Currency: true, CurrencyKnown: true, PawnKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true},
	}
	p := RoundsTradeTargets(CoreItemFacts(), need, rows, map[Resource]int64{"WoodLog": 200}, domain.Known(int64(2)))
	if len(p.Targets) != 1 || p.Targets[0].Item != "WoodLog" || p.Targets[0].MaxBuy != 150 || p.Targets[0].Stock != 200 {
		t.Fatal(p.Targets)
	}
	selection := SelectTrade(p, TradeSelectionFacts{Complete: true, Rows: withCurrency(rows), ColonySilver: 500, TraderSilver: 900, SilverKnown: true, MaxSilverSpend: 500})
	if selection.Refused || len(selection.Selected) != 1 || selection.Selected[0].Count != 150 {
		t.Fatal(selection)
	}
}
