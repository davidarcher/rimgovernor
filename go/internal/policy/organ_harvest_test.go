package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func harvestOp(part string, index int, lethal bool) SurgeryOperation {
	return SurgeryOperation{Recipe: domain.Known("RemoveBodyPart"), PartDefName: domain.Known(part), PartIndex: domain.Known(index), Kind: SurgeryHarvest,
		SuccessChance: domain.Known(0.95), EligibleDoctors: domain.Known(1), IngredientsOnMap: domain.Known(true),
		Violation: domain.Known(true), Lethal: domain.Known(lethal), YieldValue: domain.Known(organValue[part])}
}

var organValue = map[string]float64{"Kidney": 900, "Lung": 1000, "Heart": 1200, "Liver": 1000}

// harvestPrisoner is a worthless, unrecruitable prisoner with both kidneys,
// both lungs, a heart and a liver.
func harvestPrisoner(id string, goodwill int, ops ...SurgeryOperation) PrisonerFacts {
	row := prisonerRow(id, false, domain.PrisonerInteractionMaintain, 0, 1, weak)
	if ops == nil {
		ops = []SurgeryOperation{harvestOp("Kidney", 20, false), harvestOp("Kidney", 21, false), harvestOp("Lung", 18, false), harvestOp("Lung", 19, false),
			harvestOp("Heart", 16, true), harvestOp("Liver", 22, false)}
	}
	row.Operations, row.QueuedSurgeries, row.HarvestGoodwill = domain.Known(ops), domain.Known(0), domain.Known(goodwill)
	return row
}

// organUse is an ideoligion whose organ-use precept has the given effects
// (the game's HarvestedOrgan and SoldOrgan events).
func organUse(effects ...PreceptEffect) domain.Fact[Ideoligion] {
	return domain.Known(ruleIdeoligion(PreceptDef{Name: "OrganUse_Test", Effects: effects}))
}

func TestHarvestCost(t *testing.T) {
	horrible := []PreceptEffect{saw("HarvestedOrgan", -4), took("HarvestedOrgan", -15)}
	for _, c := range []struct {
		name     string
		colony   PrisonerColony
		goodwill int
		sale     bool
		cost     float64
		ok       bool
	}{
		{"no Ideology: Core thought", PrisonerColony{Colonists: 5}, -70, false, 5*5*SilverPerMoodPoint + 70*SilverPerGoodwillPoint, true},
		{"witness thought on every colonist", PrisonerColony{Colonists: 4, IdeologyActive: true, Ideology: organUse(saw("HarvestedOrgan", -5))}, 0, true, 4 * 5 * SilverPerMoodPoint, true},
		{"no effect costs nothing", PrisonerColony{Colonists: 10, IdeologyActive: true, Ideology: organUse()}, 0, true, 0, true},
		{"goodwill only", PrisonerColony{Colonists: 10, IdeologyActive: true, Ideology: organUse()}, -70, false, 70 * SilverPerGoodwillPoint, true},
		{"doer once, witnesses each", PrisonerColony{Colonists: 3, IdeologyActive: true, Ideology: organUse(horrible...)}, 0, true, (3*4 + 15) * SilverPerMoodPoint, true},
		{"sale adds its thoughts", PrisonerColony{Colonists: 3, IdeologyActive: true, Ideology: organUse(append(horrible, saw("SoldOrgan", -2), took("SoldOrgan", -8))...)}, 0, true, (3*4 + 15 + 3*2 + 8) * SilverPerMoodPoint, true},
		{"no sale ignores its thoughts", PrisonerColony{Colonists: 3, IdeologyActive: true, Ideology: organUse(append(horrible, saw("SoldOrgan", -2), took("SoldOrgan", -8))...)}, 0, false, (3*4 + 15) * SilverPerMoodPoint, true},
		{"refusal", PrisonerColony{Colonists: 3, IdeologyActive: true, Ideology: organUse(unwilling("HarvestedOrgan", nil))}, 0, false, 0, false},
		{"unread ideoligion holds", PrisonerColony{Colonists: 3, IdeologyActive: true}, 0, false, 0, false},
	} {
		cost, ok := HarvestCost(c.colony, c.goodwill, c.sale)
		if ok != c.ok || ok && cost != c.cost {
			t.Fatalf("%s: cost %v ok %v", c.name, cost, ok)
		}
	}
}

func TestSelectOrganHarvest(t *testing.T) {
	missingKidney := surgeryPawn("c", 0, restoreOp("InstallNaturalKidney", "Kidney", 20, 0.9, 1, false), restoreOp("InstallBionicKidney", "Kidney", 20, 0.9, 1, false))
	colonistNeed := OrganNeeds(domain.Known([]CarePawn{missingKidney}), SelectSurgery(domain.Known([]CarePawn{missingKidney}), nil, SurgeryContext{}).Wants, false, nil, testRecipeFacts)
	if len(colonistNeed) != 1 || colonistNeed[0] != (OrganNeed{Organ: "Kidney", For: "c", Gain: SilverPerCapacity}) {
		t.Fatalf("colonist need %+v", colonistNeed)
	}
	sale := OrganNeeds(domain.Known([]CarePawn{}), nil, true, map[Resource]int64{"Lung": 1}, testRecipeFacts)
	if len(sale) != 1 || sale[0] != (OrganNeed{Organ: "Kidney"}) {
		t.Fatalf("sale need %+v", sale)
	}
	five := PrisonerColony{Colonists: 5, BestSkill: core.BestSkill}
	for _, c := range []struct {
		name      string
		prisoners []PrisonerFacts
		colony    PrisonerColony
		needs     []OrganNeed
		inFlight  map[PawnID]bool
		want      string // "prisoner/part/for", "" for none
	}{
		{"failing kidney harvested from a low-worth prisoner", []PrisonerFacts{harvestPrisoner("p", -70)}, five, colonistNeed, nil, "p/20/c"},
		{"silver deficit sells a lung (market value beats the kidney)", []PrisonerFacts{harvestPrisoner("p", 0)}, five, OrganNeeds(domain.Known([]CarePawn{}), nil, true, nil, testRecipeFacts), nil, "p/18/"},
		{"sale refused when mood and goodwill outweigh the organ", []PrisonerFacts{harvestPrisoner("p", -70)}, PrisonerColony{Colonists: 7, BestSkill: core.BestSkill}, sale, nil, ""},
		{"colonist need before sale", []PrisonerFacts{harvestPrisoner("p", 0)}, five, append(OrganNeeds(domain.Known([]CarePawn{}), nil, true, nil, testRecipeFacts), colonistNeed...), nil, "p/20/c"},
		{"abhorrent precept refuses", []PrisonerFacts{harvestPrisoner("p", 0)}, PrisonerColony{Colonists: 1, BestSkill: core.BestSkill, IdeologyActive: true, Ideology: organUse(unwilling("HarvestedOrgan", nil))}, colonistNeed, nil, ""},
		{"acceptable precept prices only goodwill", []PrisonerFacts{harvestPrisoner("p", -70)}, PrisonerColony{Colonists: 30, BestSkill: core.BestSkill, IdeologyActive: true, Ideology: organUse()}, sale, nil, "p/20/"},
		{"recruitable worthy prisoner is not harvested", []PrisonerFacts{func() PrisonerFacts {
			row := harvestPrisoner("p", 0)
			row.Recruitable, row.Prospect = domain.Known(true), domain.Known(strong)
			return row
		}()}, five, colonistNeed, nil, ""},
		{"last kidney is never taken", []PrisonerFacts{harvestPrisoner("p", 0, harvestOp("Kidney", 21, false))}, five, colonistNeed, nil, ""},
		{"lethal harvest refused", []PrisonerFacts{harvestPrisoner("p", 0, harvestOp("Kidney", 20, true), harvestOp("Kidney", 21, true))}, five, colonistNeed, nil, ""},
		{"a queued harvest waits", []PrisonerFacts{harvestPrisoner("p", 0), func() PrisonerFacts {
			row := harvestPrisoner("q", 0)
			row.QueuedSurgeries = domain.Known(1)
			return row
		}()}, five, colonistNeed, nil, ""},
		{"an open harvest action waits", []PrisonerFacts{harvestPrisoner("p", 0)}, five, colonistNeed, map[PawnID]bool{"p": true}, ""},
		{"unknown goodwill refuses", []PrisonerFacts{func() PrisonerFacts {
			row := harvestPrisoner("p", 0)
			row.HarvestGoodwill = domain.Unknown[int]()
			return row
		}()}, five, colonistNeed, nil, ""},
	} {
		t.Run(c.name, func(t *testing.T) {
			h, ok := SelectOrganHarvest(domain.Known(c.prisoners), domain.Known(c.colony), c.needs, c.inFlight)
			got := ""
			if ok {
				got = string(h.Prisoner) + "/" + itoa(h.Part) + "/" + string(h.For)
				if h.Recipe != "RemoveBodyPart" || h.Gain <= h.Cost {
					t.Fatalf("harvest %+v", h)
				}
			}
			if got != c.want {
				t.Fatalf("got %q (%+v), want %q", got, h, c.want)
			}
		})
	}
}

func itoa(n int) string {
	if n < 10 {
		return string(rune('0' + n))
	}
	return itoa(n/10) + string(rune('0'+n%10))
}

func TestOrganSaleSurplus(t *testing.T) {
	need := domain.Known(TradeNeed{MedicineReplenish: 10})
	stock := domain.Known([]Amount{{Resource: "Kidney", Count: 1}, {Resource: "Silver", Count: 0}})
	got, _ := OrganSaleSurplus(CoreItemFacts(), need, stock, domain.Known[int64](3), testRecipeFacts).Value()
	if len(got.Surplus) != 1 || got.Surplus[0] != (Amount{Resource: "Kidney", Count: 1}) || got.Retained["Kidney"] != 0 {
		t.Fatalf("short silver: %+v", got)
	}
	rich := domain.Known([]Amount{{Resource: "Kidney", Count: 1}, {Resource: "Silver", Count: 100000}})
	if got, _ := OrganSaleSurplus(CoreItemFacts(), need, rich, domain.Known[int64](3), testRecipeFacts).Value(); len(got.Surplus) != 0 {
		t.Fatalf("silver held: %+v", got)
	}
}

func TestReserveSurgeryStockHoldsQueuedInstallPart(t *testing.T) {
	need := domain.Known(TradeNeed{MedicineReplenish: 10})
	stock := domain.Known([]Amount{{Resource: "Kidney", Count: 1}, {Resource: "Silver", Count: 0}})
	sale := OrganSaleSurplus(CoreItemFacts(), need, stock, domain.Known[int64](3), testRecipeFacts)
	pawn := surgeryPawn("a", 1, restoreOp("InstallNaturalKidney", "Kidney", 20, 0.9, 0, true))
	pawn.QueuedRecipes = []string{"InstallNaturalKidney"}
	pawn.QueuedItems = []Resource{"Kidney"}
	got, _ := ReserveSurgeryStock(sale, domain.Known([]CarePawn{pawn})).Value()
	if len(got.Surplus) != 0 || got.Retained["Kidney"] != 1 {
		t.Fatalf("queued install keeps its kidney: %+v", got)
	}
}

func TestReserveSurgeryStockKeepsOneKidneyPerWant(t *testing.T) {
	need := domain.Known(TradeNeed{MedicineReplenish: 10})
	stock := domain.Known([]Amount{{Resource: "Kidney", Count: 2}, {Resource: "Silver", Count: 0}})
	sale := OrganSaleSurplus(CoreItemFacts(), need, stock, domain.Known[int64](3), testRecipeFacts)
	pawns := domain.Known([]CarePawn{surgeryPawn("a", 0, restoreOp("InstallNaturalKidney", "Kidney", 20, 0.9, 0, true))})
	got, _ := ReserveSurgeryStock(sale, pawns).Value()
	if len(got.Surplus) != 1 || got.Surplus[0] != (Amount{Resource: "Kidney", Count: 1}) || got.Retained["Kidney"] != 1 {
		t.Fatalf("one kidney reserved, one sold: %+v", got)
	}
	if got, _ := ReserveSurgeryStock(sale, domain.Known([]CarePawn{})).Value(); got.Surplus[0].Count != 2 {
		t.Fatalf("no want: %+v", got)
	}
}
