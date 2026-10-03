package policy

import "testing"

func offerRow(line string, def string, gender string, price float64) TradeSheetRowFact {
	return TradeSheetRowFact{LineID: line, DefName: def, Pawn: true, PawnKnown: true, PawnGender: gender, TraderCount: 1,
		TraderWillTrade: true, TraderWillTradeKnown: true, BuyPrice: price, BuyPriceKnown: true}
}

func milkWants(owned []UpkeepAnimal, rows []TradeSheetRowFact) []HerdWant {
	in := milkInput(owned)
	in.Offers = HerdOffers(rows, in.Races)
	return HerdWants(PlanHerd(in))
}

func TestSelectAnimalPurchaseBuysTraderCowForMilkPlanAboveReserve(t *testing.T) {
	rows := []TradeSheetRowFact{offerRow("b", "Cow", "Female", 300), offerRow("a", "Cow", "Female", 300), offerRow("x", "Yak", "Female", 10)}
	wants := milkWants(goatHerd(), rows)
	if len(wants) == 0 || wants[0].Race != "Cow" || !wants[0].Male || !wants[0].Female {
		t.Fatal("a milk plan wants cows", wants)
	}
	got, ok := SelectAnimalPurchase(wants, rows, 1000, 500, nil)
	if !ok || got.LineID != "a" || got.Count != 1 {
		t.Fatal(got, ok)
	}
	// An earlier selected line's spend counts against the reserve.
	if _, ok := SelectAnimalPurchase(wants, rows, 1000, 500, []TradeSelectionLine{{LineID: "b", Count: 1}}); ok {
		t.Fatal("bought below the reserve")
	}
}

func TestSelectAnimalPurchasePrefersFoundersMissingSex(t *testing.T) {
	owned := append(goatHerd(), planAnimal("c1", "Cow", "Female"))
	rows := []TradeSheetRowFact{offerRow("f", "Cow", "Female", 100), offerRow("m", "Cow", "Male", 250)}
	in := milkInput(owned, wildOf("Cow"))
	wants := HerdWants(PlanHerd(in))
	if len(wants) == 0 || wants[0] != (HerdWant{Race: "Cow", Male: true}) {
		t.Fatal("a lone cow's mate is wanted", wants)
	}
	got, ok := SelectAnimalPurchase(wants, rows, 1000, 0, nil)
	if !ok || got.LineID != "m" {
		t.Fatal(got, ok)
	}
}

func TestSelectAnimalPurchaseNothingWithoutWantOrSilver(t *testing.T) {
	rows := []TradeSheetRowFact{offerRow("a", "Cow", "Female", 300)}
	if _, ok := SelectAnimalPurchase(nil, rows, 10000, 0, nil); ok {
		t.Fatal("bought with no want")
	}
	wants := milkWants(goatHerd(), rows)
	if _, ok := SelectAnimalPurchase(wants, rows, 500, 0, nil); ok {
		t.Fatal("bought past half the silver")
	}
	rows[0].ColonyCount = 1
	if _, ok := SelectAnimalPurchase(wants, rows, 10000, 0, nil); ok {
		t.Fatal("bought a row the colony side holds")
	}
}

func TestHerdOffersAreTraderPawnRowsOfCatalogRaces(t *testing.T) {
	rows := []TradeSheetRowFact{offerRow("a", "Cow", "Female", 1), offerRow("b", "Cow", "Male", 1), offerRow("c", "Yak", "Male", 1), {LineID: "d", DefName: "Goat", TraderCount: 1}}
	got := HerdOffers(rows, raceCatalog(milkRace("Cow", 12, 2.5), milkRace("Goat", 2, 0.8)))
	if len(got) != 1 || got[0] != "Cow" {
		t.Fatal(got)
	}
}
