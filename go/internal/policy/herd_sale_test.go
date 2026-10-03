package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func bondedAs(a UpkeepAnimal, bonded bool) UpkeepAnimal {
	a.Bonded = domain.Known(bonded)
	return a
}

// retiredGoatsPlan is the plan where cows (a pair) have replaced the goats.
func retiredGoatsPlan(goats []UpkeepAnimal) ([]UpkeepAnimal, HerdPlan) {
	rows := append(goats, bondedAs(planAnimal("c1", "Cow", "Male"), false), bondedAs(planAnimal("c2", "Cow", "Female"), false))
	return rows, PlanHerd(milkInput(rows, wildOf("Cow")))
}

func TestHerdSaleAnimalsRetiredRaceSellsExceptBonded(t *testing.T) {
	goats := []UpkeepAnimal{
		bondedAs(planAnimal("g1", "Goat", "Male"), false),
		bondedAs(planAnimal("g2", "Goat", "Female"), true),
		bondedAs(juvenile(planAnimal("g3", "Goat", "Female")), false),
	}
	rows, plan := retiredGoatsPlan(goats)
	if !plan.Policy.Retired["Goat"] {
		t.Fatal("goats should retire", plan.Policy)
	}
	got := HerdSaleAnimals(domain.Known(rows), plan.Policy)
	if want := map[PawnID]bool{"g1": true, "g3": true}; !reflect.DeepEqual(got, want) {
		t.Fatalf("sale = %v, want %v (bonded stays, young sells, cows stay)", got, want)
	}
}

func TestHerdSaleAnimalsUnknownFactsSellNothing(t *testing.T) {
	goats := []UpkeepAnimal{bondedAs(planAnimal("g1", "Goat", "Male"), false), planAnimal("g2", "Goat", "Female")}
	rows, plan := retiredGoatsPlan(goats)
	if got := HerdSaleAnimals(domain.Known(rows), plan.Policy); !reflect.DeepEqual(got, map[PawnID]bool{"g1": true}) {
		t.Fatalf("unknown bond sold: %v", got)
	}
	if got := HerdSaleAnimals(domain.Unknown[[]UpkeepAnimal](), plan.Policy); len(got) != 0 {
		t.Fatalf("unread census sold: %v", got)
	}
	rows[0].Slaughter = domain.Unknown[bool]()
	if got := HerdSaleAnimals(domain.Known(rows), plan.Policy); len(got) != 0 {
		t.Fatalf("unknown designation sold: %v", got)
	}
}

func TestHerdSaleAnimalsFoundersAndTrainedNeededStay(t *testing.T) {
	// A lone cow is a founder: no ceiling, never listed.
	owned := append(goatHerd(), bondedAs(planAnimal("c1", "Cow", "Female"), false))
	for i := range owned[:3] {
		owned[i] = bondedAs(owned[i], false)
	}
	plan := PlanHerd(milkInput(owned, wildOf("Cow")))
	if got := HerdSaleAnimals(domain.Known(owned), plan.Policy); got["c1"] {
		t.Fatal("founder sold", got)
	}
	// Over the ceiling, a trained animal of a kept race is needed.
	policy := HerdPolicy{PopulationMax: map[Resource]int64{"Goat": 3}}
	rows := []UpkeepAnimal{
		bondedAs(planAnimal("g1", "Goat", "Male"), false), bondedAs(planAnimal("g2", "Goat", "Female"), false),
		bondedAs(planAnimal("g3", "Goat", "Female"), false), bondedAs(hauler(planAnimal("g4", "Goat", "Female")), false),
	}
	if got := HerdSaleAnimals(domain.Known(rows), policy); !reflect.DeepEqual(got, map[PawnID]bool{"g2": true}) {
		t.Fatalf("one over the ceiling sells one untrained goat: %v", got)
	}
	// Both untrained females go before the hauler is considered; with only
	// bonded or trained animals left to cut, nothing sells.
	rows[1], rows[2] = bondedAs(rows[1], true), bondedAs(rows[2], true)
	if got := HerdSaleAnimals(domain.Known(rows), policy); len(got) != 0 {
		t.Fatalf("bonded or trained animal sold: %v", got)
	}
}

func animalRow(line, def, pawn string, sell float64) TradeSheetRowFact {
	row := tradeRow(line, def, 1, 0, 0, sell)
	row.Pawn, row.PawnID, row.ProtectedExport = true, pawn, true
	return row
}

func TestSelectTradeSellsSurplusAnimals(t *testing.T) {
	rows := []TradeSheetRowFact{
		animalRow("#0", "Goat", "g1", 40),
		animalRow("#1", "Goat", "g2", 40),
		animalRow("#2", "Cow", "c1", 200),
		tradeRow("#3", "Silver", 100, 1000, 1, 1),
	}
	facts := tradeFacts(rows, 100, 1000, 100)
	facts.SaleAnimals = map[string]bool{"g1": true, "g2": true}
	s := SelectTrade(domain.TradeEconomicPolicy{}, facts)
	if want := []TradeSelectionLine{{"#0", "Goat", -1}, {"#1", "Goat", -1}}; s.Refused || !reflect.DeepEqual(s.Selected, want) {
		t.Fatalf("selected = %+v (%s), want %+v (the cow is not for sale)", s.Selected, s.Reason, want)
	}
	// Unknown pawn classification, or a missing price, never sells.
	unknown := append([]TradeSheetRowFact(nil), rows...)
	unknown[0].PawnKnown = false
	unknown[1].SellPriceKnown = false
	facts.Rows = unknown
	if s := SelectTrade(domain.TradeEconomicPolicy{}, facts); len(s.Selected) != 0 {
		t.Fatalf("unknown row facts sold: %+v", s.Selected)
	}
	// The trader's cash bounds the sale.
	facts.Rows, facts.TraderSilver = rows, 50
	if s := SelectTrade(domain.TradeEconomicPolicy{}, facts); len(s.Selected) != 1 {
		t.Fatalf("trader cash ignored: %+v", s.Selected)
	}
}

// Selling animals leaves the silver reserve rule alone: purchases still
// spend only silver above the reserve, with no credit for the sale.
func TestSelectTradeAnimalSaleKeepsSilverReserve(t *testing.T) {
	rows := []TradeSheetRowFact{
		tradeRow("#0", "MedicineIndustrial", 0, 20, 30, 1),
		animalRow("#1", "Goat", "g1", 400),
		tradeRow("#2", "Silver", 250, 1000, 1, 1),
	}
	target := domain.TradeEconomicPolicy{SilverReserve: 200, Targets: []domain.TradeTarget{tradeTarget("MedicineIndustrial", 10, 10, 0, 50, 0)}}
	facts := tradeFacts(rows, 250, 1000, 250)
	facts.SaleAnimals = map[string]bool{"g1": true}
	s := SelectTrade(target, facts)
	if want := []TradeSelectionLine{{"#0", "MedicineIndustrial", 1}, {"#1", "Goat", -1}}; s.Refused || !reflect.DeepEqual(s.Selected, want) {
		t.Fatalf("selected = %+v (%s), want %+v", s.Selected, s.Reason, want)
	}
}

func TestAnimalSaleNeedNeedsSilverShortage(t *testing.T) {
	short := ReviewTradeNeed(MedicalReserveReview{Replenish: domain.Known(int64(5))}, domain.Known([]Amount{}), nil, nil, domain.Unknown[WealthFacts](), RoutineTradePolicy{})
	sale := map[PawnID]bool{"g1": true, "g2": true}
	colonists := domain.Known(int64(3))
	need, _ := AnimalSaleNeed(short, sale, domain.Known(int64(100)), colonists).Value()
	if need.SurplusAnimals != 2 || !need.Any() {
		t.Fatalf("short silver: %+v", need)
	}
	if need, _ := AnimalSaleNeed(short, sale, domain.Known(int64(5000)), colonists).Value(); need.SurplusAnimals != 0 {
		t.Fatalf("silver not short: %+v", need)
	}
	if need, _ := AnimalSaleNeed(short, sale, domain.Unknown[int64](), colonists).Value(); need.SurplusAnimals != 0 {
		t.Fatalf("unknown silver: %+v", need)
	}
	if need, _ := AnimalSaleNeed(short, nil, domain.Known(int64(100)), colonists).Value(); need.SurplusAnimals != 0 {
		t.Fatalf("no sale animals: %+v", need)
	}
}
