package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// tradeNeed replays the TradeNeed DetectRounds measured for
// TradeWithCaravan (the medicine reserve, resource census, floors and food
// ledger of the recorded review).
func tradeNeed(t *testing.T, r Rounds) policy.TradeNeed {
	t.Helper()
	facts := r.Facts.MedicalReserve
	facts.Colonists = r.Facts.Colonists
	medicine, err := policy.ReviewMedicalReserve(facts, r.Latches.MedicalReserve, r.Policy.MedicalReserve)
	if err != nil {
		t.Fatal(err)
	}
	need, known := policy.ReviewTradeNeed(r.Facts.Items.Currency, medicine, r.Facts.Resources, r.Policy.ResourceTargets, policy.RoundsTradeFloors(r.Policy, nil), r.Facts.Wealth, r.Policy.Trade, policy.RoundsTradeFood(r.Facts, r.Policy)).Value()
	if !known {
		t.Fatal("trade need unknown")
	}
	return need
}

func loadTrade(t *testing.T, path string) Rounds {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	a, err := r.Assessment(policy.TradeWithCaravan)
	if err != nil || a.Finding != domain.FindingUnmet {
		t.Fatal("TradeWithCaravan not in deficit with the caravan present", a, err)
	}
	return r
}

// Recorded from acceptance run trade/routine (issue #234) at 04b0a98cb,
// tick 7521: the fixture's colony has no medicine and a trader caravan
// has arrived. The review opens TradeWithCaravan on the medicine
// shortfall; once the caravan leaves the goal recovers.
func TestReplayCaravanOpensTradeOnMedicineShortfall(t *testing.T) {
	r := loadTrade(t, "testdata/trade-caravan-medicine-shortfall.json")
	if need := tradeNeed(t, r); need.MedicineReplenish <= 0 {
		t.Fatalf("no medicine shortfall measured: %+v", need)
	}
	r.Facts.Traders = domain.Known([]policy.TraderFacts{})
	if a, err := r.Assessment(policy.TradeWithCaravan); err != nil || a.Finding == domain.FindingUnmet {
		t.Fatal("trade stood with no caravan on the map", a, err)
	}
}

// Recorded from acceptance run trade/routine-stocked at 04b0a98cb, tick
// 9655: 2000 steel, recorded under the since-deleted
// --routine-item-wealth-share 0.01; the item share is raised past the
// constant 0.6 (#875). The wealth rule sells exactly stock minus the
// retained 500-steel floor.
func TestReplaySteelHoardSellsDownToTheFloor(t *testing.T) {
	r := loadTrade(t, "testdata/trade-steel-hoard-wealth-surplus.json")
	r.Facts.Wealth = domain.Known(policy.WealthFacts{Items: 15000, Buildings: 3500, Pawns: 1500, Total: 20000})
	need := tradeNeed(t, r)
	if len(need.Surplus) != 1 || need.Surplus[0] != (policy.Amount{Resource: "Steel", Count: 1500}) || need.Retained["Steel"] != 500 {
		t.Fatalf("steel surplus %+v retained %v, want 1500 sold keeping 500", need.Surplus, need.Retained)
	}
}

// Recorded from acceptance run trade/routine-food-bridge at 04b0a98cb,
// tick 2515: one day of food on hand. The shared food ledger asks the
// caravan for a food bridge (which good -- pemmican first -- is
// tradeFoodTargets' choice over the live sheet, covered in policy).
func TestReplayOneDayOfFoodBuysABridge(t *testing.T) {
	r := loadTrade(t, "testdata/trade-food-bridge-one-day.json")
	// The caravan is unpriced: the review asks the negotiator to browse it.
	if need := tradeNeed(t, r); !need.Food.Browse || need.Food.Nutrition != 0 {
		t.Fatalf("no browse measured: %+v", need.Food)
	}
	// Once its sheet is recorded the plan opens the food bridge it prices.
	plan, _ := r.Facts.FoodPlan.Value()
	bridge := policy.FoodChannel{Kind: policy.FoodTrade, ID: "caravan", StockCap: domain.Known(int64(60)), LeadDays: domain.Known(0.0)}
	plan.Portfolio = append(plan.Portfolio, policy.FoodPlanEntry{Channel: bridge, Decision: policy.FoodPlanOpen})
	r.Facts.FoodPlan = domain.Known(plan)
	if need := tradeNeed(t, r); need.Food.Nutrition != 60 || need.Food.Browse {
		t.Fatalf("no food bridge measured: %+v", need.Food)
	}
}

// Recorded from acceptance run trade/routine-food-surplus at 04b0a98cb,
// tick 9000: 6000 rice against a RawRice:5000 MaintainResource target and
// an active fine-meal bill with no protein. The review sells the crop
// surplus above the floor and buys the missing meat.
func TestReplayCropSurplusBuysMissingProteinAboveTheFloor(t *testing.T) {
	need := tradeNeed(t, loadTrade(t, "testdata/trade-food-crop-surplus-for-meat.json"))
	if len(need.Surplus) != 1 || need.Surplus[0] != (policy.Amount{Resource: "RawRice", Count: 1000}) || need.Retained["RawRice"] != 5000 {
		t.Fatalf("rice surplus %+v retained %v, want 1000 sold keeping 5000", need.Surplus, need.Retained)
	}
	if len(need.Food.Missing) == 0 {
		t.Fatalf("missing protein not measured: %+v", need.Food)
	}
	meat := false
	for _, alt := range need.Food.Missing[0].Alternatives {
		meat = meat || alt == "meat"
	}
	if !meat {
		t.Fatalf("missing slot does not take meat: %+v", need.Food.Missing)
	}
}
