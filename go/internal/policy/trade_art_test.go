package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The owed room keeps the best packed sculpture; the rest is for sale
// (#1194), and NextSculpture installs that same best piece.
func TestSaleSculpturesReserveTheBest(t *testing.T) {
	obs, rooms, _ := upgradeFixture(t, RoomQuality{Wealth: 3000, Beauty: -1, Space: 25, Impressiveness: 35})
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	packed := []PackedSculpture{
		{ID: "Thing_A", Def: SculptureDefinition, Quality: 2, MarketValue: 300},
		{ID: "Thing_B", Def: SculptureDefinition, Quality: 5, MarketValue: 900},
		{ID: "Thing_C", Def: SculptureDefinition, Quality: -1},
	}
	sale := SaleSculptures(obs, targets, rooms, packed)
	if want := map[string]bool{"Thing_A": true, "Thing_C": true}; !reflect.DeepEqual(sale, want) {
		t.Fatalf("sale = %v, want %v", sale, want)
	}
	if s, ok := NextSculpture(obs, targets, rooms, packed); !ok || s.Packed != "Thing_B" {
		t.Fatalf("install = %+v %v", s, ok)
	}
	// No owed room: everything sells.
	if sale := SaleSculptures(obs, nil, rooms, packed); len(sale) != 3 {
		t.Fatalf("no owed room sale = %v", sale)
	}
}

func artRow(line, thing string, sell float64) TradeSheetRowFact {
	row := tradeRow(line, SculptureDefinition, 1, 0, 0, sell)
	row.ThingID = thing
	return row
}

func TestSelectTradeSellsSurplusArt(t *testing.T) {
	rows := []TradeSheetRowFact{
		tradeRow("#0", "Steel", 200, 0, 2, 1),
		artRow("#1", "Thing_A", 150),
		artRow("#2", "Thing_B", 400),
		tradeRow("#3", "Silver", 500, 1000, 1, 1),
	}
	target := domain.TradeEconomicPolicy{Targets: []domain.TradeTarget{tradeTarget("Steel", 50, 0, 150, 0, 1)}}
	cases := []struct {
		name     string
		sale     map[string]bool
		first    bool
		trader   int64
		selected []TradeSelectionLine
	}{
		{"surplus art sells after goods", map[string]bool{"Thing_A": true, "Thing_B": true}, false, 1000,
			[]TradeSelectionLine{{"#0", "Steel", -150}, {"#1", SculptureDefinition, -1}, {"#2", SculptureDefinition, -1}}},
		{"reserved art is kept", map[string]bool{"Thing_A": true}, false, 1000,
			[]TradeSelectionLine{{"#0", "Steel", -150}, {"#1", SculptureDefinition, -1}}},
		{"trader cash runs out on art after goods", map[string]bool{"Thing_A": true}, false, 200,
			[]TradeSelectionLine{{"#0", "Steel", -150}}},
		{"negative headroom sells art first", map[string]bool{"Thing_A": true}, true, 200,
			[]TradeSelectionLine{{"#1", SculptureDefinition, -1}, {"#0", "Steel", -50}}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			facts := tradeFacts(rows, 500, tc.trader, 500)
			facts.SaleArt, facts.ArtFirst = tc.sale, tc.first
			s := SelectTrade(target, facts)
			if s.Refused || !reflect.DeepEqual(s.Selected, tc.selected) {
				t.Fatalf("selected = %+v (%s), want %+v", s.Selected, s.Reason, tc.selected)
			}
		})
	}
}

// Art alone sells without a catalog target; a protected or unpriced art
// row does not.
func TestSelectTradeArtWithoutTargets(t *testing.T) {
	protected := artRow("#2", "Thing_B", 400)
	protected.ProtectedExport = true
	unpriced := artRow("#3", "Thing_C", 0)
	unpriced.SellPriceKnown = false
	facts := tradeFacts([]TradeSheetRowFact{artRow("#1", "Thing_A", 150), protected, unpriced}, 0, 1000, 0)
	facts.SaleArt = map[string]bool{"Thing_A": true, "Thing_B": true, "Thing_C": true}
	s := SelectTrade(domain.TradeEconomicPolicy{}, facts)
	if want := []TradeSelectionLine{{"#1", SculptureDefinition, -1}}; s.Refused || !reflect.DeepEqual(s.Selected, want) {
		t.Fatalf("selected = %+v (%s)", s.Selected, s.Reason)
	}
}
