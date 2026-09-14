package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tradeRow builds a fully-known, ordinarily tradeable sheet row; each test
// then spoils exactly the one field it is about, so an assertion can never
// pass because some unrelated flag happened to be zero.
func tradeRow(line, def string, colony, trader int64, buy, sell float64) TradeSheetRowFact {
	return TradeSheetRowFact{
		LineID: line, DefName: def, ColonyCount: colony, TraderCount: trader,
		BuyPrice: buy, BuyPriceKnown: true, SellPrice: sell, SellPriceKnown: true,
		TraderWillTrade: true, TraderWillTradeKnown: true,
		CurrencyKnown: true, PawnKnown: true, ProtectedExportKnown: true,
	}
}

func tradeTarget(item string, stock, buy, sell int64, maxBuy, minSell float64) domain.TradeTarget {
	return domain.TradeTarget{Item: item, Stock: stock, MaxBuy: buy, MaxSell: sell, MaxBuyPrice: maxBuy, MinSellPrice: minSell}
}

func tradeFacts(rows []TradeSheetRowFact, colony, trader, spend int64) TradeSelectionFacts {
	return TradeSelectionFacts{Complete: true, Rows: rows, ColonySilver: colony, TraderSilver: trader, SilverKnown: true, MaxSilverSpend: spend}
}

func selectedCounts(t *testing.T, s TradeSelection) map[string]int64 {
	t.Helper()
	if s.Refused {
		t.Fatalf("selection refused unexpectedly: %s", s.Reason)
	}
	out := map[string]int64{}
	for _, line := range s.Selected {
		if line.Count == 0 {
			t.Fatalf("selected line %q carries no adjustment", line.LineID)
		}
		if _, seen := out[line.DefName]; seen {
			t.Fatalf("definition %q selected twice", line.DefName)
		}
		out[line.DefName] = line.Count
	}
	return out
}

func evidenceFor(t *testing.T, s TradeSelection, item string) TradeSelectionEvidence {
	t.Helper()
	for _, row := range s.Evidence {
		if row.Item == item {
			return row
		}
	}
	t.Fatalf("no evidence row for %q in %+v", item, s.Evidence)
	return TradeSelectionEvidence{}
}

func TestSelectTradeBuysUpToTheBindingCap(t *testing.T) {
	// Each case leaves exactly one of the four buy caps binding, so a dropped
	// term in the min() would change the answer.
	for _, test := range []struct {
		name          string
		stock, supply int64
		maxBuy        int64
		silver        int64
		want          int64
	}{
		{"deficit binds", 90, 1000, 1000, 10000, 10},
		{"trader supply binds", 0, 7, 1000, 10000, 7},
		{"policy max buy binds", 0, 1000, 12, 10000, 12},
		{"budget binds", 0, 1000, 1000, 25, 5},
	} {
		t.Run(test.name, func(t *testing.T) {
			policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 100, test.maxBuy, 0, 9, 0)}}
			got := SelectTrade(policy, tradeFacts([]TradeSheetRowFact{tradeRow("#1", "Steel", test.stock, test.supply, 5, 4)}, test.silver, 500, 100000))
			if counts := selectedCounts(t, got); counts["Steel"] != test.want {
				t.Fatalf("bought %d, want %d", counts["Steel"], test.want)
			}
		})
	}
}

func TestSelectTradeSpendsPriorityOrderNotSheetOrder(t *testing.T) {
	// Silver buys exactly one of either item; the second target must get
	// nothing, and the order that decides is the policy's, not the sheet's.
	rows := []TradeSheetRowFact{tradeRow("#1", "Steel", 0, 50, 60, 1), tradeRow("#2", "Gold", 0, 50, 60, 1)}
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{
		tradeTarget("Gold", 10, 10, 0, 100, 0), tradeTarget("Steel", 10, 10, 0, 100, 0),
	}}
	got := selectedCounts(t, SelectTrade(policy, tradeFacts(rows, 60, 500, 100000)))
	if want := map[string]int64{"Gold": 1}; !reflect.DeepEqual(got, want) {
		t.Fatalf("selected %v, want %v (the first-listed target must win the budget)", got, want)
	}
}

func TestSelectTradeCarriesFractionalBudgetBetweenLines(t *testing.T) {
	// Three lines at 1.5 silver from a budget of 4: flooring each subtraction
	// to whole silver would wrongly afford a third unit.
	rows := []TradeSheetRowFact{tradeRow("#1", "Steel", 0, 1, 1.5, 1), tradeRow("#2", "Gold", 0, 1, 1.5, 1), tradeRow("#3", "Jade", 0, 1, 1.5, 1)}
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{
		tradeTarget("Steel", 5, 5, 0, 10, 0), tradeTarget("Gold", 5, 5, 0, 10, 0), tradeTarget("Jade", 5, 5, 0, 10, 0),
	}}
	got := selectedCounts(t, SelectTrade(policy, tradeFacts(rows, 4, 500, 100000)))
	if want := map[string]int64{"Steel": 1, "Gold": 1}; !reflect.DeepEqual(got, want) {
		t.Fatalf("selected %v, want %v (4 silver buys two units at 1.5, not three)", got, want)
	}
}

func TestSelectTradeSellsSurplusCappedByTraderCash(t *testing.T) {
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 100, 0, 1000, 0, 2)}}
	got := SelectTrade(policy, tradeFacts([]TradeSheetRowFact{tradeRow("#1", "Steel", 500, 0, 9, 3)}, 0, 21, 100000))
	if counts := selectedCounts(t, got); counts["Steel"] != -7 {
		t.Fatalf("sold %d, want -7 (21 silver of trader cash at 3 each)", counts["Steel"])
	}
	row := evidenceFor(t, got, "Steel")
	if !row.Matched || row.EligibleStock != 500 || row.RetainedTarget != 100 || row.ExportCapacity != 400 || row.Count != -7 {
		t.Fatalf("sale evidence %+v does not describe the decision", row)
	}
}

func TestSelectTradeDrainsTraderCashAcrossSales(t *testing.T) {
	rows := []TradeSheetRowFact{tradeRow("#1", "Steel", 100, 0, 9, 10), tradeRow("#2", "Gold", 100, 0, 9, 10)}
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{
		tradeTarget("Steel", 0, 0, 1000, 0, 1), tradeTarget("Gold", 0, 0, 1000, 0, 1),
	}}
	got := selectedCounts(t, SelectTrade(policy, tradeFacts(rows, 0, 30, 100000)))
	if want := map[string]int64{"Steel": -3}; !reflect.DeepEqual(got, want) {
		t.Fatalf("selected %v, want %v (the first sale spends all 30 of the trader's silver)", got, want)
	}
}

func TestSelectTradeRespectsPriceLimits(t *testing.T) {
	buy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 100, 100, 0, 4, 0)}}
	got := SelectTrade(buy, tradeFacts([]TradeSheetRowFact{tradeRow("#1", "Steel", 0, 100, 4.01, 1)}, 100000, 500, 100000))
	if counts := selectedCounts(t, got); len(counts) != 0 {
		t.Fatalf("bought %v above the max buy price", counts)
	}
	if row := evidenceFor(t, got, "Steel"); !row.Matched || row.Count != 0 {
		t.Fatalf("an over-priced buy is a matched zero, not a blocker: %+v", row)
	}
	sell := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 0, 0, 100, 0, 4)}}
	got = SelectTrade(sell, tradeFacts([]TradeSheetRowFact{tradeRow("#1", "Steel", 100, 0, 9, 3.99)}, 0, 100000, 100000))
	if counts := selectedCounts(t, got); len(counts) != 0 {
		t.Fatalf("sold %v below the min sell price", counts)
	}
}

func TestSelectTradeHonoursReservesAndSpendCeiling(t *testing.T) {
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 1000, 1000, 0, 10, 0)}, SilverReserve: 400}
	rows := []TradeSheetRowFact{tradeRow("#1", "Steel", 0, 10000, 1, 1)}

	facts := tradeFacts(rows, 1000, 500, 100000)
	got := SelectTrade(policy, facts)
	if got.SilverReserve != 400 || got.Budget != 600 {
		t.Fatalf("reserve %d budget %d, want 400/600", got.SilverReserve, got.Budget)
	}
	if counts := selectedCounts(t, got); counts["Steel"] != 600 {
		t.Fatalf("bought %d, want 600 (1000 silver less a 400 reserve)", counts["Steel"])
	}

	// A silver floor above the policy's own reserve wins.
	facts.Floors = map[string]int64{"Silver": 900}
	if got = SelectTrade(policy, facts); got.SilverReserve != 900 || got.Budget != 100 {
		t.Fatalf("reserve %d budget %d, want 900/100 (the economic floor outranks the policy reserve)", got.SilverReserve, got.Budget)
	}

	// The request's own ceiling caps spend below the affordable amount.
	facts.Floors = nil
	facts.MaxSilverSpend = 50
	if got = SelectTrade(policy, facts); got.Budget != 50 {
		t.Fatalf("budget %d, want 50 (the request ceiling binds)", got.Budget)
	}

	// A reserve above the colony's silver is a zero budget, never a negative one.
	facts.MaxSilverSpend, facts.ColonySilver = 100000, 100
	if got = SelectTrade(policy, facts); got.Budget != 0 || len(got.Selected) != 0 {
		t.Fatalf("budget %d with %d lines, want 0/0 when the reserve exceeds the colony's silver", got.Budget, len(got.Selected))
	}

	// Halted silver production forbids spending any of it.
	facts.ColonySilver, facts.Stopped = 100000, []string{"Silver"}
	if got = SelectTrade(policy, facts); got.Budget != 0 || len(got.Selected) != 0 {
		t.Fatalf("budget %d with %d lines, want 0/0 while silver is production-halted", got.Budget, len(got.Selected))
	}
}

func TestSelectTradeFloorOutranksTargetStock(t *testing.T) {
	// Stock 300 is above the target's own 100 but below the construction floor
	// of 400, which turns what would have been a sale into a purchase.
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 100, 1000, 1000, 10, 1)}}
	facts := tradeFacts([]TradeSheetRowFact{tradeRow("#1", "Steel", 300, 1000, 1, 5)}, 100000, 100000, 100000)
	facts.Floors = map[string]int64{"Steel": 400}
	got := SelectTrade(policy, facts)
	if counts := selectedCounts(t, got); counts["Steel"] != 100 {
		t.Fatalf("selected %d, want +100 (the floor of 400 outranks the target stock of 100)", counts["Steel"])
	}
	if row := evidenceFor(t, got, "Steel"); row.RetainedTarget != 400 || row.ExportCapacity != 0 {
		t.Fatalf("evidence %+v should retain 400 and export nothing", row)
	}
}

func TestSelectTradeWillNotSellProductionHaltedItems(t *testing.T) {
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 0, 0, 1000, 0, 1)}}
	facts := tradeFacts([]TradeSheetRowFact{tradeRow("#1", "Steel", 500, 0, 9, 5)}, 0, 100000, 100000)
	facts.Stopped = []string{"Steel"}
	got := SelectTrade(policy, facts)
	if counts := selectedCounts(t, got); len(counts) != 0 {
		t.Fatalf("sold %v of a production-halted definition", counts)
	}
	if row := evidenceFor(t, got, "Steel"); !row.Matched || row.Count != 0 {
		t.Fatalf("evidence %+v should be a matched zero", row)
	}
}

func TestSelectTradeBlocksUnusableRows(t *testing.T) {
	spoil := map[string]func(*TradeSheetRowFact){
		tradeBlockerRow:      func(r *TradeSheetRowFact) { r.TraderWillTrade = false },
		"will-trade unknown": func(r *TradeSheetRowFact) { r.TraderWillTradeKnown = false },
		"pawn":               func(r *TradeSheetRowFact) { r.Pawn = true },
		"pawn unknown":       func(r *TradeSheetRowFact) { r.PawnKnown = false },
		"currency":           func(r *TradeSheetRowFact) { r.Currency = true },
		"currency unknown":   func(r *TradeSheetRowFact) { r.CurrencyKnown = false },
	}
	for name, spoiler := range spoil {
		t.Run(name, func(t *testing.T) {
			row := tradeRow("#1", "Steel", 0, 1000, 1, 1)
			spoiler(&row)
			policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 100, 100, 100, 10, 1)}}
			got := SelectTrade(policy, tradeFacts([]TradeSheetRowFact{row}, 100000, 100000, 100000))
			if len(got.Selected) != 0 {
				t.Fatalf("selected %+v from an ineligible row", got.Selected)
			}
			if evidence := evidenceFor(t, got, "Steel"); evidence.Blocker != tradeBlockerRow || evidence.Matched {
				t.Fatalf("evidence %+v, want the ineligible-row blocker", evidence)
			}
		})
	}
}

func TestSelectTradeBlocksProtectedExports(t *testing.T) {
	for name, spoiler := range map[string]func(*TradeSheetRowFact){
		"protected": func(r *TradeSheetRowFact) { r.ProtectedExport = true },
		"unknown":   func(r *TradeSheetRowFact) { r.ProtectedExportKnown = false },
	} {
		t.Run(name, func(t *testing.T) {
			row := tradeRow("#1", "MealSimple", 500, 0, 9, 5)
			spoiler(&row)
			policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("MealSimple", 100, 0, 1000, 0, 1)}}
			got := SelectTrade(policy, tradeFacts([]TradeSheetRowFact{row}, 0, 100000, 100000))
			if len(got.Selected) != 0 {
				t.Fatalf("selected %+v of a protected export", got.Selected)
			}
			if evidence := evidenceFor(t, got, "MealSimple"); evidence.Blocker != tradeBlockerProtected {
				t.Fatalf("evidence %+v, want the protected-export blocker", evidence)
			}
		})
	}
	// The same protection must not block a purchase of the same definition.
	row := tradeRow("#1", "MealSimple", 0, 500, 5, 9)
	row.ProtectedExport = true
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("MealSimple", 100, 1000, 0, 10, 0)}}
	got := SelectTrade(policy, tradeFacts([]TradeSheetRowFact{row}, 100000, 100000, 100000))
	if counts := selectedCounts(t, got); counts["MealSimple"] != 100 {
		t.Fatalf("bought %d, want 100: an export protection never blocks an import", counts["MealSimple"])
	}
}

func TestSelectTradeBlocksAmbiguousAndAbsentDefinitions(t *testing.T) {
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 100, 100, 100, 10, 1)}}

	got := SelectTrade(policy, tradeFacts(nil, 100000, 100000, 100000))
	if evidence := evidenceFor(t, got, "Steel"); evidence.Blocker != tradeBlockerAmbiguous || evidence.Matched {
		t.Fatalf("evidence %+v for an absent definition, want the ambiguous blocker", evidence)
	}

	rows := []TradeSheetRowFact{tradeRow("#1", "Steel", 0, 10, 1, 1), tradeRow("#2", "Steel", 500, 10, 1, 1)}
	got = SelectTrade(policy, tradeFacts(rows, 100000, 100000, 100000))
	if len(got.Selected) != 0 {
		t.Fatalf("selected %+v when two rows carry the same definition", got.Selected)
	}
	if evidence := evidenceFor(t, got, "Steel"); evidence.Blocker != tradeBlockerAmbiguous {
		t.Fatalf("evidence %+v, want the ambiguous blocker for a duplicated definition", evidence)
	}
}

func TestSelectTradeBlocksUnknownQuantitiesAndPrices(t *testing.T) {
	buy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 100, 100, 0, 10, 0)}}
	sell := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 0, 0, 100, 0, 1)}}
	for _, test := range []struct {
		name   string
		policy domain.TradeEconomicPolicy
		row    TradeSheetRowFact
	}{
		{"colony count unknown", buy, TradeSheetRowFact{LineID: "#1", DefName: "Steel", ColonyCount: -1, TraderCount: 10, BuyPrice: 1, BuyPriceKnown: true, SellPrice: 1, SellPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true, ProtectedExportKnown: true}},
		{"trader count unknown", buy, TradeSheetRowFact{LineID: "#1", DefName: "Steel", ColonyCount: 0, TraderCount: -1, BuyPrice: 1, BuyPriceKnown: true, SellPrice: 1, SellPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, PawnKnown: true, ProtectedExportKnown: true}},
		{"buy price unknown", buy, func() TradeSheetRowFact { r := tradeRow("#1", "Steel", 0, 10, 1, 1); r.BuyPriceKnown = false; return r }()},
		{"buy price not finite", buy, func() TradeSheetRowFact { r := tradeRow("#1", "Steel", 0, 10, 1, 1); r.BuyPrice = math.NaN(); return r }()},
		{"sell price unknown", sell, func() TradeSheetRowFact {
			r := tradeRow("#1", "Steel", 500, 0, 9, 5)
			r.SellPriceKnown = false
			return r
		}()},
		{"sell price not finite", sell, func() TradeSheetRowFact {
			r := tradeRow("#1", "Steel", 500, 0, 9, 5)
			r.SellPrice = math.Inf(1)
			return r
		}()},
	} {
		t.Run(test.name, func(t *testing.T) {
			got := SelectTrade(test.policy, tradeFacts([]TradeSheetRowFact{test.row}, 100000, 100000, 100000))
			if len(got.Selected) != 0 {
				t.Fatalf("selected %+v on unknown evidence", got.Selected)
			}
			if evidence := evidenceFor(t, got, "Steel"); evidence.Blocker != tradeBlockerUnknown || evidence.Matched {
				t.Fatalf("evidence %+v, want the unknown-evidence blocker", evidence)
			}
		})
	}
}

func TestSelectTradeRefusesUnusableFacts(t *testing.T) {
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 100, 100, 0, 10, 0)}}
	rows := []TradeSheetRowFact{tradeRow("#1", "Steel", 0, 1000, 1, 1)}
	for _, test := range []struct {
		name  string
		spoil func(*TradeSelectionFacts)
	}{
		{"incomplete sheet", func(f *TradeSelectionFacts) { f.Complete = false }},
		{"silver unknown", func(f *TradeSelectionFacts) { f.SilverKnown = false }},
		{"colony silver unknown", func(f *TradeSelectionFacts) { f.ColonySilver = -1 }},
		{"trader silver unknown", func(f *TradeSelectionFacts) { f.TraderSilver = -1 }},
		{"negative spend ceiling", func(f *TradeSelectionFacts) { f.MaxSilverSpend = -1 }},
	} {
		t.Run(test.name, func(t *testing.T) {
			facts := tradeFacts(rows, 100000, 100000, 100000)
			test.spoil(&facts)
			got := SelectTrade(policy, facts)
			if !got.Refused || got.Reason == "" || len(got.Selected) != 0 || len(got.Evidence) != 0 {
				t.Fatalf("got %+v, want an explained refusal with no lines and no evidence", got)
			}
		})
	}
	// An invalid policy is refused before any sheet is read at all.
	if got := SelectTrade(domain.TradeEconomicPolicy{}, tradeFacts(rows, 100000, 100000, 100000)); !got.Refused {
		t.Fatalf("got %+v, want a refusal for a policy with no targets", got)
	}
}

func TestEconomicReservesFoldsPolicyTargetsAndConstruction(t *testing.T) {
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{
		tradeTarget("Steel", 400, 100, 100, 10, 1), // below the player's own reserve
		tradeTarget("Gold", 50, 100, 100, 10, 1),   // above it
		tradeTarget("Jade", 25, 100, 100, 10, 1),   // no reserve of its own
		tradeTarget("Cloth", 0, 100, 100, 10, 1),   // no floor from any source
	}}
	floors, stopped := EconomicReserves(policy, TradeReserveFacts{
		Reserves:     map[string]int64{"Steel": 500, "Gold": 10, "Silver": 0, "Plasteel": -5},
		Restricted:   []string{"Wood", "Cloth"},
		Construction: map[string]int64{"Steel": 200, "Jade": 5, "Uranium": 0},
	})
	want := map[string]int64{"Steel": 700, "Gold": 50, "Jade": 30}
	if !reflect.DeepEqual(floors, want) {
		t.Fatalf("floors %v, want %v", floors, want)
	}
	if !reflect.DeepEqual(stopped, []string{"Cloth", "Wood"}) {
		t.Fatalf("stopped %v, want a sorted [Cloth Wood]", stopped)
	}
}

func TestEconomicReservesDoesNotAliasItsInputs(t *testing.T) {
	reserves := map[string]int64{"Steel": 100}
	restricted := []string{"Wood"}
	policy := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 900, 1, 1, 1, 1)}}
	floors, stopped := EconomicReserves(policy, TradeReserveFacts{Reserves: reserves, Restricted: restricted})
	floors["Steel"], stopped[0] = 1, "Steel"
	if reserves["Steel"] != 100 || restricted[0] != "Wood" {
		t.Fatalf("EconomicReserves handed back its own caller's storage: %v %v", reserves, restricted)
	}
}
