package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestReviewTradeNeedMeasuresShortfallsAndSurplus(t *testing.T) {
	medicine := MedicalReserveReview{Active: true, Replenish: domain.Known(int64(4))}
	resources := domain.Known([]Amount{{Resource: "Steel", Count: 500}, {Resource: ComponentResource, Count: 2}, {Resource: "WoodLog", Count: 40}})
	targets := map[Resource]int64{"Steel": 200, "WoodLog": 100}
	need, known := ReviewTradeNeed(medicine, resources, targets, RoutineTradePolicy{ComponentTarget: 10}).Value()
	if !known || need.MedicineReplenish != 4 || need.ComponentShortfall != 8 || len(need.Surplus) != 1 || need.Surplus[0] != (Amount{Resource: "Steel", Count: 300}) {
		t.Fatal(need, known)
	}
	if _, known = ReviewTradeNeed(MedicalReserveReview{}, resources, targets, RoutineTradePolicy{}).Value(); known {
		t.Fatal("unknown medicine reserve became a trade need")
	}
	if need, known = ReviewTradeNeed(MedicalReserveReview{Replenish: domain.Known(int64(0))}, resources, nil, RoutineTradePolicy{}).Value(); !known || need.Any() {
		t.Fatal(need, known)
	}
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

func TestRoutineTradeTargetsBuyCheapestMedicineThenSellSurplus(t *testing.T) {
	rows := []TradeSheetRowFact{
		{LineID: "#0", DefName: "MedicineIndustrial", ColonyCount: 1, TraderCount: 10, BuyPrice: 18, BuyPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true, ProtectedExportKnown: true, ProtectedExport: true},
		{LineID: "#1", DefName: "MedicineHerbal", ColonyCount: 0, TraderCount: 10, BuyPrice: 9, BuyPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true, ProtectedExportKnown: true, ProtectedExport: true},
		{LineID: "#2", DefName: "Steel", ColonyCount: 500, TraderCount: 0, SellPrice: 1.2, SellPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true, ProtectedExportKnown: true},
		{LineID: "#3", DefName: "Silver", ColonyCount: 100, TraderCount: 900, Currency: true, CurrencyKnown: true, PawnKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, ProtectedExportKnown: true},
	}
	need := TradeNeed{MedicineReplenish: 4, Surplus: []Amount{{Resource: "Steel", Count: 300}}}
	p := RoutineTradeTargets(need, rows, map[Resource]int64{"Steel": 200}, RoutineTradePolicy{SilverReserve: 20})
	if err := p.Validate(); err != nil {
		t.Fatal(err)
	}
	if len(p.Targets) != 2 || p.Targets[0].Item != "MedicineHerbal" || p.Targets[0].MaxBuy != 4 || p.Targets[0].Stock != 4 || p.Targets[1].Item != "Steel" || p.Targets[1].MaxSell != 300 || p.Targets[1].Stock != 200 {
		t.Fatal(p.Targets)
	}
	selection := SelectTrade(p, TradeSelectionFacts{Complete: true, Rows: rows, ColonySilver: 100, TraderSilver: 900, SilverKnown: true, MaxSilverSpend: 80})
	if selection.Refused || len(selection.Selected) != 2 || selection.Selected[0] != (TradeSelectionLine{LineID: "#1", DefName: "MedicineHerbal", Count: 4}) || selection.Selected[1] != (TradeSelectionLine{LineID: "#2", DefName: "Steel", Count: -300}) {
		t.Fatal(selection)
	}
	if p = RoutineTradeTargets(TradeNeed{MedicineReplenish: 4}, rows[2:], nil, RoutineTradePolicy{}); len(p.Targets) != 0 {
		t.Fatal("trader without medicine produced a target", p.Targets)
	}
}
