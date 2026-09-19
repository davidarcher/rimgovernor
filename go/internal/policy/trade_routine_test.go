package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestReviewTradeNeedMeasuresShortfallsAndSurplus(t *testing.T) {
	medicine := MedicalReserveReview{Active: true, Replenish: domain.Known(int64(4))}
	resources := domain.Known([]Amount{{Resource: "Steel", Count: 500}, {Resource: ComponentResource, Count: 2}, {Resource: "WoodLog", Count: 40}})
	targets := map[Resource]int64{"Steel": 200, "WoodLog": 100}
	need, known := ReviewTradeNeed(medicine, resources, targets, nil, domain.Unknown[WealthFacts](), RoutineTradePolicy{ComponentTarget: 10}).Value()
	if !known || need.MedicineReplenish != 4 || need.ComponentShortfall != 8 || len(need.Surplus) != 1 || need.Surplus[0] != (Amount{Resource: "Steel", Count: 300}) {
		t.Fatal(need, known)
	}
	if _, known = ReviewTradeNeed(MedicalReserveReview{}, resources, targets, nil, domain.Unknown[WealthFacts](), RoutineTradePolicy{}).Value(); known {
		t.Fatal("unknown medicine reserve became a trade need")
	}
	if need, known = ReviewTradeNeed(MedicalReserveReview{Replenish: domain.Known(int64(0))}, resources, nil, nil, domain.Unknown[WealthFacts](), RoutineTradePolicy{}).Value(); !known || need.Any() {
		t.Fatal(need, known)
	}
}

func TestWealthSurplusSellsHoardsDownToTheFloor(t *testing.T) {
	stock := []Amount{{Resource: "Steel", Count: 2000}, {Resource: "Plasteel", Count: 100}, {Resource: "Gold", Count: 80}, {Resource: "Silver", Count: 5000}, {Resource: ComponentResource, Count: 90}}
	heavy := domain.Known(WealthFacts{Items: 30000, Buildings: 10000, Pawns: 8000, Total: 48000})
	p := RoutineTradePolicy{ItemWealthShare: 0.5}
	if got := WealthSurplus(stock, nil, nil, domain.Unknown[WealthFacts](), p); got != nil {
		t.Fatal("unknown wealth produced a surplus", got)
	}
	if got := WealthSurplus(stock, nil, nil, heavy, RoutineTradePolicy{}); got != nil {
		t.Fatal("a zero share produced a surplus", got)
	}
	light := domain.Known(WealthFacts{Items: 10000, Buildings: 30000, Pawns: 8000, Total: 48000})
	if got := WealthSurplus(stock, nil, nil, light, p); got != nil {
		t.Fatal("an item share under the threshold produced a surplus", got)
	}
	// Above the threshold the default retained minimum is the only floor:
	// plasteel sits exactly on it and silver and components stay out.
	want := []Amount{{Resource: "Steel", Count: 1500}, {Resource: "Gold", Count: 30}}
	if got := WealthSurplus(stock, nil, nil, heavy, p); !equalAmounts(got, want) {
		t.Fatal(got, want)
	}
	// An economic floor above the retained minimum raises the keep; a
	// construction deficit raises it again; a floor at stock yields nothing.
	floors, _ := EconomicReserves(domain.TradeEconomicPolicy{}, TradeReserveFacts{Reserves: map[string]int64{"Steel": 800}})
	if got := WealthSurplus(stock, nil, floors, heavy, p); !equalAmounts(got, []Amount{{Resource: "Steel", Count: 1200}, {Resource: "Gold", Count: 30}}) {
		t.Fatal(got)
	}
	floors, _ = EconomicReserves(domain.TradeEconomicPolicy{}, TradeReserveFacts{Reserves: map[string]int64{"Steel": 800}, Construction: map[string]int64{"Steel": 400, "Gold": 80}})
	if got := WealthSurplus(stock, nil, floors, heavy, p); !equalAmounts(got, []Amount{{Resource: "Steel", Count: 800}}) {
		t.Fatal(got)
	}
	floors, _ = EconomicReserves(domain.TradeEconomicPolicy{}, TradeReserveFacts{Reserves: map[string]int64{"Steel": 2000, "Gold": 80}})
	if got := WealthSurplus(stock, nil, floors, heavy, p); got != nil {
		t.Fatal("stock at the floor produced a surplus", got)
	}
	// A MaintainResource target and the policy's own table are floors too;
	// a named table replaces the default one entirely.
	if got := WealthSurplus(stock, map[Resource]int64{"Steel": 1900}, nil, heavy, RoutineTradePolicy{ItemWealthShare: 0.5, RetainedMinimum: map[Resource]int64{"Gold": 70}}); !equalAmounts(got, []Amount{{Resource: "Steel", Count: 100}, {Resource: "Plasteel", Count: 100}, {Resource: "Gold", Count: 10}}) {
		t.Fatal(got)
	}
	if got := WealthSurplus(stock, nil, nil, domain.Known(WealthFacts{Items: 100, Total: 0}), p); got != nil {
		t.Fatal("zero total wealth produced a surplus", got)
	}
}

func TestReviewTradeNeedMergesWealthSurplusBehindTargets(t *testing.T) {
	medicine := MedicalReserveReview{Replenish: domain.Known(int64(0))}
	resources := domain.Known([]Amount{{Resource: "Steel", Count: 2000}, {Resource: "Gold", Count: 80}})
	heavy := domain.Known(WealthFacts{Items: 30000, Buildings: 10000, Pawns: 8000, Total: 48000})
	p := RoutineTradePolicy{ItemWealthShare: 0.5}
	// The steel target wins over the wealth rule's retained minimum; gold
	// comes from the wealth rule alone, retained at its floor.
	need, known := ReviewTradeNeed(medicine, resources, map[Resource]int64{"Steel": 1000}, nil, heavy, p).Value()
	if !known || !equalAmounts(need.Surplus, []Amount{{Resource: "Steel", Count: 1000}, {Resource: "Gold", Count: 30}}) || need.Retained["Steel"] != 1000 || need.Retained["Gold"] != 50 {
		t.Fatal(need, known)
	}
	if need, known = ReviewTradeNeed(medicine, resources, nil, nil, domain.Unknown[WealthFacts](), p).Value(); !known || need.Any() {
		t.Fatal("unknown wealth should leave only the target-driven surplus", need, known)
	}
	rows := []TradeSheetRowFact{{LineID: "l1", DefName: "Gold", ColonyCount: 80, SellPrice: 30, SellPriceKnown: true}}
	economic := RoutineTradeTargets(TradeNeed{Surplus: []Amount{{Resource: "Gold", Count: 30}}, Retained: map[Resource]int64{"Gold": 50}}, rows, nil, p)
	if len(economic.Targets) != 1 || economic.Targets[0].Stock != 50 || economic.Targets[0].MaxSell != 30 {
		t.Fatal("the wealth surplus sale should retain its floor as the target stock", economic.Targets)
	}
	if err := (RoutineTradePolicy{ItemWealthShare: 1.5}).Validate(); err == nil {
		t.Fatal("share past 1 validated")
	}
	if err := (RoutineTradePolicy{RetainedMinimum: map[Resource]int64{"Steel": -1}}).Validate(); err == nil {
		t.Fatal("negative retained minimum validated")
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
