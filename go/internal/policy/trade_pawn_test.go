package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func pawnRow(line string, price float64, violent bool, skills ...ProfileSkill) TradeSheetRowFact {
	return TradeSheetRowFact{
		LineID: line, DefName: "Human", TraderCount: 1, BuyPrice: price, BuyPriceKnown: true,
		TraderWillTrade: true, TraderWillTradeKnown: true, Pawn: true, PawnKnown: true, CurrencyKnown: true,
		Skills: skills, ViolenceCapable: violent, ViolenceCapableKnown: true,
	}
}

func pawnCapacity(t *testing.T, f JoinerCapacityFacts) bool {
	t.Helper()
	room, known := JoinerCapacity(f).Value()
	if !known {
		t.Fatal("capacity unknown")
	}
	return room
}

func TestSelectPawnPurchaseStagesBelowTargetWithCapacityAndSilver(t *testing.T) {
	rows := []TradeSheetRowFact{
		pawnRow("#1", 400, true, ProfileSkill{Name: "Shooting", Level: 8}),
		pawnRow("#2", 400, true, ProfileSkill{Name: "Shooting", Level: 8, Passion: "Major"}),
		pawnRow("#3", 100, false, ProfileSkill{Name: "Shooting", Level: 20, Passion: "Major"}),
	}
	line, ok := SelectPawnPurchase(pawnCapacity(t, joinerFacts(t, 3)), rows, 1000, 100, nil)
	if !ok || line != (TradeSelectionLine{LineID: "#2", DefName: "Human", Count: 1}) {
		t.Fatalf("line = %+v, %v; want the passionate violence-capable pawn", line, ok)
	}
}

func TestSelectPawnPurchasePrefersValuePerSilver(t *testing.T) {
	rows := []TradeSheetRowFact{
		pawnRow("#1", 450, true, ProfileSkill{Name: "Mining", Level: 10}),
		pawnRow("#2", 100, true, ProfileSkill{Name: "Mining", Level: 5}),
	}
	line, ok := SelectPawnPurchase(true, rows, 1000, 0, nil)
	if !ok || line.LineID != "#2" {
		t.Fatalf("line = %+v, %v; want the cheaper pawn with more value per silver", line, ok)
	}
}

func TestSelectPawnPurchaseRefusesWithoutCapacityOrBudget(t *testing.T) {
	rows := []TradeSheetRowFact{pawnRow("#1", 400, true, ProfileSkill{Name: "Shooting", Level: 8})}
	atTarget := joinerFacts(t, int(domain.PopulationTarget))
	noBed := joinerFacts(t, 3)
	noBed.Sleeping = joinerBeds()
	noFood := joinerFacts(t, 3)
	noFood.FoodDays = domain.Known(1.0)
	for name, f := range map[string]JoinerCapacityFacts{"at target": atTarget, "no bed": noBed, "no food": noFood} {
		if line, ok := SelectPawnPurchase(pawnCapacity(t, f), rows, 1000, 0, nil); ok {
			t.Fatalf("%s: staged %+v", name, line)
		}
	}
	room := pawnCapacity(t, joinerFacts(t, 3))
	// 50% of 700 is 350: below the 400 price.
	if line, ok := SelectPawnPurchase(room, rows, 700, 0, nil); ok {
		t.Fatalf("half-silver budget: staged %+v", line)
	}
	// Silver above half the price but the reserve leaves 300.
	if line, ok := SelectPawnPurchase(room, rows, 1000, 700, nil); ok {
		t.Fatalf("below reserve: staged %+v", line)
	}
	// Resource lines already spend what the reserve leaves.
	spent := []TradeSelectionLine{{LineID: "#9", DefName: "Steel", Count: 100}}
	withSteel := append([]TradeSheetRowFact{{LineID: "#9", DefName: "Steel", BuyPrice: 3, BuyPriceKnown: true}}, rows...)
	if line, ok := SelectPawnPurchase(room, withSteel, 1000, 400, spent); ok {
		t.Fatalf("after resource spend: staged %+v", line)
	}
}

func TestPopulationTradeNeedOpensOnlyWithKnownCapacity(t *testing.T) {
	need := domain.Known(TradeNeed{})
	if n, _ := PopulationTradeNeed(need, domain.Known(true)).Value(); !n.Any() {
		t.Fatal("capacity did not raise the trade need")
	}
	for _, capacity := range []domain.Fact[bool]{domain.Known(false), domain.Unknown[bool]()} {
		if n, known := PopulationTradeNeed(need, capacity).Value(); !known || n.Any() {
			t.Fatalf("capacity %v raised the need", capacity)
		}
	}
}
