package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestMealTierUsesSeasonalTargetAndOnlyActiveBill(t *testing.T) {
	r := tierRequest()
	r.MinDays = 20
	r.TargetDays = 24
	r.RawRunwayDays = domain.Known(18.0)
	r.Paste = domain.Unknown[Infrastructure]()
	review, err := ReviewMealTier(r, tierBenches())
	if err != nil || review.Tier != MealSimple {
		t.Fatal(review, err)
	}
	benches, _ := tierBenches().Value()
	benches[0].Bills = []ExistingProductionBill{{Recipe: "CookMealLavish", Active: domain.Known(false)}, {Recipe: "CookMealFine", Active: domain.Known(true)}}
	if tier := ObservedMealTier(benches); tier != MealFine {
		t.Fatal(tier)
	}
}

func TestMealMoodLeverNeedsObservedExpectationsPressure(t *testing.T) {
	p := MoodPawn{HighExpectations: domain.Known(true), Mood: domain.Known(.3), Target: domain.Known(.5)}
	rows := mealMoodProvision(p, nil)
	if len(rows) != 1 || rows[0].Goal != EnsureCooking || rows[0].Offset >= 0 {
		t.Fatal(rows)
	}
	p.Mood = domain.Known(.6)
	if rows = mealMoodProvision(p, nil); len(rows) != 0 {
		t.Fatal(rows)
	}
}

func TestMealReplacementPreservesPlayerBills(t *testing.T) {
	r := tierRequest()
	r.RawRunwayDays = domain.Known(1.0)
	r.Paste = domain.Unknown[Infrastructure]()
	for _, managed := range []bool{false, true} {
		benches, _ := tierBenches().Value()
		benches[0].Bills = []ExistingProductionBill{{ID: "fine-owned", Recipe: "CookMealFine", Managed: domain.Known(managed)}}
		selected, ok := SelectProductionBill(CookFood, domain.Known(benches), domain.Known(int64(2)), r.RawRunwayDays, domain.Unknown[float64](), r.TargetDays, ProductionBillContext{Meals: &r})
		if !ok || selected.Recipe != "CookMealSimple" || (selected.Replace != "") != managed {
			t.Fatal(managed, selected, ok)
		}
	}
}

func TestStoredIngredientsDoNotInventProduction(t *testing.T) {
	supply := FoodSupply{Stocks: []FoodStock{{ID: "milk", Holder: domain.Known(PawnID("")), Nutrition: domain.Known(10.0), RawClass: domain.Known(IngredientAnimalProduct)}, {ID: "rice", Holder: domain.Known(PawnID("")), Nutrition: domain.Known(20.0), RawClass: domain.Known(IngredientVegetable)}}}
	r := tierRequest()
	plan, _ := r.Plan.Value()
	plan.Portfolio = nil
	plan.DeliveredPerDay = 0
	for _, channel := range StockIngredientChannels(supply) {
		rate, _ := channel.NutritionPerDay.Value()
		if rate != 0 {
			t.Fatal(channel)
		}
		plan.Portfolio = append(plan.Portfolio, FoodPlanEntry{Channel: channel, Decision: FoodPlanHold})
	}
	r.Plan = domain.Known(plan)
	review, err := ReviewMealTier(r, tierBenches())
	if err != nil || review.Tier != MealFine {
		t.Fatal(review, err)
	}
}

func TestPasteSiteNeedsConnectedFirmNetworkAndHopper(t *testing.T) {
	meals := tierRequest()
	meals.RawRunwayDays = domain.Known(1.0)
	paste := PasteSiteRequest{Meals: meals, Benches: tierBenches(), Hopper: domain.Known(Infrastructure{Name: "Hopper", Available: domain.Known(true)}), Size: domain.Known(Bounds{Width: 3, Height: 4}), Power: domain.Known(PowerTopology{Buildings: []PowerSite{{Cell: domain.Cell{X: 15, Z: 10}, PowerBuilding: PowerBuilding{Network: domain.Known("grid"), Connected: domain.Known(true)}}}})}
	r := SiteTypeRequest{Paste: &paste}
	for x := int32(6); x <= 14; x++ {
		for z := int32(6); z <= 14; z++ {
			r.Field.Site.Cells = append(r.Field.Site.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)})
		}
	}
	plan, ok := PlanSiteType(r)
	if !ok || plan.Kind != SitePaste || len(plan.Buildings) != 2 || plan.Buildings[1].Definition != "Hopper" {
		t.Fatal(plan, ok)
	}
	paste.Hopper = domain.Unknown[Infrastructure]()
	if _, ok = PlanSiteType(r); ok {
		t.Fatal("unknown hopper admitted")
	}
}
