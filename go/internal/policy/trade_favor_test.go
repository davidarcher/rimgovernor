package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func favorGoldRow(stock int64, price float64) TradeSheetRowFact {
	return TradeSheetRowFact{
		LineID: "#2", DefName: "Gold", ColonyCount: stock, TraderCount: 99999,
		SellPrice: price, SellPriceKnown: true,
		TraderWillTrade: true, TraderWillTradeKnown: true, Currency: false, CurrencyKnown: true,
		Pawn: false, PawnKnown: true, ProtectedExport: false, ProtectedExportKnown: true,
	}
}

func favorFacts(rows ...TradeSheetRowFact) TradeSelectionFacts {
	favor := TradeSheetRowFact{LineID: "#1", DefName: "", Currency: true, CurrencyKnown: true, TraderCount: 99999}
	return TradeSelectionFacts{Complete: true, Rows: append([]TradeSheetRowFact{favor}, rows...), Favor: true}
}

func TestSelectFavorSaleSellsGoldAboveKeepWithNoSilver(t *testing.T) {
	got := SelectFavorSale(favorFacts(favorGoldRow(200, 0.5)), 50)
	if got.Refused || len(got.Selected) != 1 || got.Selected[0] != (TradeSelectionLine{LineID: "#2", DefName: "Gold", Count: -150}) {
		t.Fatalf("selection %+v", got)
	}
	if got.Evidence[0].RetainedTarget != 50 {
		t.Fatalf("evidence %+v", got.Evidence)
	}
}

func TestSelectFavorSaleSellsNothingOnUnknownOrIneligibleGold(t *testing.T) {
	unpriced := favorGoldRow(200, 0)
	unpriced.SellPriceKnown = false
	unknownStock := favorGoldRow(-1, 1)
	refuses := favorGoldRow(200, 1)
	refuses.TraderWillTrade = false
	protected := favorGoldRow(200, 1)
	protected.ProtectedExportKnown = false
	for name, facts := range map[string]TradeSelectionFacts{
		"no gold row":   favorFacts(),
		"at keep":       favorFacts(favorGoldRow(50, 1)),
		"unpriced":      favorFacts(unpriced),
		"unknown stock": favorFacts(unknownStock),
		"won't trade":   favorFacts(refuses),
		"protected":     favorFacts(protected),
	} {
		if got := SelectFavorSale(facts, 50); got.Refused || len(got.Selected) != 0 {
			t.Fatalf("%s: selected %+v", name, got)
		}
	}
	if got := SelectFavorSale(TradeSelectionFacts{}, 50); !got.Refused {
		t.Fatal("an incomplete sheet was not refused")
	}
}

// goldKept protects 50 gold through a construction demand.
var goldKept = TradeRetained(domain.Known(ResourceConsumption{WindowDays: 15}), nil, map[Resource]int64{"Gold": 50})

func goldStock(n int64) domain.Fact[[]Amount] {
	return domain.Known([]Amount{{Resource: "Gold", Count: n}})
}

func TestFavorGoldNeedRequiresACollectorAndGoldAboveKeep(t *testing.T) {
	collector := domain.Known([]TraderFacts{{ID: "t", Kind: TributeCollectorKind, CanTrade: true}})
	merchant := domain.Known([]TraderFacts{{ID: "t", Kind: "Caravan_Outlander_BulkGoods", CanTrade: true}})
	base := domain.Known(TradeNeed{})
	need := func(traders domain.Fact[[]TraderFacts], stock domain.Fact[[]Amount]) int64 {
		n, _ := FavorGoldNeed(base, traders, stock, nil, nil, goldKept).Value()
		return n.FavorGold
	}
	if got := need(collector, goldStock(120)); got != 70 {
		t.Fatalf("collector with 120 gold needs %d, want 70 above the protected 50", got)
	}
	for name, got := range map[string]int64{
		"no collector":  need(merchant, goldStock(120)),
		"gold at keep":  need(collector, goldStock(50)),
		"unknown stock": need(collector, domain.Unknown[[]Amount]()),
		"no census":     need(domain.Unknown[[]TraderFacts](), goldStock(120)),
	} {
		if got != 0 {
			t.Fatalf("%s: need %d", name, got)
		}
	}
	floors := map[string]int64{"Gold": 100}
	if n, _ := FavorGoldNeed(base, collector, goldStock(120), nil, floors, noRetained).Value(); n.FavorGold != 20 {
		t.Fatalf("floor ignored: %+v", n)
	}
}

// Snapshot: the goal stands for a collector with gold in stock and not
// without one or with no gold.
func TestTradeWithCaravanStandsForCollectorWithGold(t *testing.T) {
	f := stableRounds()
	f.ResourceConsumption = domain.Known(ResourceConsumption{WindowDays: 15})
	f.Resources = domain.Known([]Amount{{Resource: "Gold", Count: 300}})
	f.Traders = domain.Known([]TraderFacts{{ID: "c", Kind: TributeCollectorKind, CanTrade: true}})
	if got := needs(t, f, RoundsLatches{}); !assessedDeficit(got, TradeWithCaravan) {
		t.Fatal("a collector with gold in stock opened no caravan goal", got)
	}
	f.Traders = domain.Known([]TraderFacts{{ID: "c", Kind: "Caravan_Outlander_BulkGoods", CanTrade: true}})
	if got := needs(t, f, RoundsLatches{}); assessedDeficit(got, TradeWithCaravan) {
		t.Fatal("a non-collector with gold in stock stood the goal", got)
	}
	f.Traders = domain.Known([]TraderFacts{{ID: "c", Kind: TributeCollectorKind, CanTrade: true}})
	f.Resources = domain.Known([]Amount{})
	if got := needs(t, f, RoundsLatches{}); assessedDeficit(got, TradeWithCaravan) {
		t.Fatal("no gold stood the goal", got)
	}
}
