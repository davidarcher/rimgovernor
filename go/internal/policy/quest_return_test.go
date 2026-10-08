package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func returnFixture() WorldSite {
	return WorldSite{ID: "site", Security: domain.Known(SiteSecurity{Known: domain.Known(true), TrapCount: domain.Known(int32(0)), ActiveThreat: domain.Known(false), DormantThreat: domain.Known(false)}), Extraction: domain.Known(SiteExtraction{CanReform: domain.Known(true), Crew: []domain.PawnID{"a"}, CarryCapacity: domain.Known(35.0), CarriedMass: domain.Known(6.0), InventoryFoodDays: domain.Known(3.0), CrewNutritionPerDay: domain.Known(1.0), Cargo: []SiteCargo{{ID: "food", Def: "MealSurvivalPack", Count: domain.Known(int64(10)), UnitMass: domain.Known(0.3), MarketValue: domain.Known(20.0), Nutrition: domain.Known(1.0), Held: domain.Known(true)}, {ID: "silver", Def: "Silver", Count: domain.Known(int64(30)), UnitMass: domain.Known(0.008), MarketValue: domain.Known(1.0), Nutrition: domain.Known(0.0), Held: domain.Known(false)}}, HomeRoutes: []SiteHomeRoute{{Map: 0, Tile: 12, Reachable: domain.Known(true), TravelTicks: domain.Known(int64(domain.TicksPerDay))}}})}
}

func TestClearedSiteReformsLootAndRetainedFoodHome(t *testing.T) {
	site := returnFixture()
	plan := PlanSiteReturn(site, false)
	if plan.Reason != "" || plan.Departure == nil || plan.Departure.DestinationTile() != 12 {
		t.Fatal(plan)
	}
	cargo := plan.Departure.Cargo()
	if len(cargo) != 2 || cargo[0].Definition != "MealSurvivalPack" || cargo[0].Count != 10 || cargo[1].Definition != "Silver" || cargo[1].Count != 30 {
		t.Fatal(cargo)
	}
	plan = PlanSiteReturn(site, true)
	if plan.Departure == nil || len(plan.Departure.Cargo()) != 1 {
		t.Fatal(plan)
	}
}

func TestFailedFightRequestsPhysicalRetreatWithoutReform(t *testing.T) {
	site := returnFixture()
	extraction, _ := site.Extraction.Value()
	extraction.CanReform = domain.Known(false)
	site.Extraction = domain.Known(extraction)
	plan := PlanSiteReturn(site, true)
	if !plan.Retreat || plan.Departure != nil || plan.HomeTile != 12 || len(plan.Crew) != 1 {
		t.Fatal(plan)
	}
	if plan := PlanSiteReturn(site, false); plan.Reason != "site_fight_pending" || plan.Retreat {
		t.Fatal(plan)
	}
}

func TestSiteReturnHoldsWhenFoodOrNativeRouteMissing(t *testing.T) {
	site := returnFixture()
	extraction, _ := site.Extraction.Value()
	extraction.InventoryFoodDays = domain.Known(0.0)
	extraction.Cargo[0].Held = domain.Known(false)
	extraction.Cargo[0].Nutrition = domain.Known(0.0)
	site.Extraction = domain.Known(extraction)
	if plan := PlanSiteReturn(site, false); plan.Reason != "food_capacity" || plan.Departure != nil {
		t.Fatal(plan)
	}
	extraction.HomeRoutes[0].Reachable = domain.Known(false)
	site.Extraction = domain.Known(extraction)
	if plan := PlanSiteReturn(site, false); plan.Reason != "home_route_unknown" {
		t.Fatal(plan)
	}
}

func TestStoppedExpeditionReturnsOnlyJournaledCrew(t *testing.T) {
	trip := ExpeditionTrip{Tile: domain.Known(int32(10)), PawnIDs: []domain.PawnID{"survivor"}, HomeRoutes: []SiteHomeRoute{{Tile: 12, Reachable: domain.Known(true), TravelTicks: domain.Known(int64(60000))}}}
	if plan := PlanStoppedExpeditionReturn(trip, []domain.PawnID{"survivor", "lost"}); plan == nil || plan.DestinationTile() != 12 || len(plan.Cargo()) != 0 {
		t.Fatal(plan)
	}
	if plan := PlanStoppedExpeditionReturn(trip, []domain.PawnID{"unrelated"}); plan != nil {
		t.Fatal(plan)
	}
	trip.Destination = domain.Known(int32(10))
	if plan := PlanStoppedExpeditionReturn(trip, []domain.PawnID{"survivor"}); plan != nil {
		t.Fatal(plan)
	}
	trip.Destination = domain.Unknown[int32]()
	trip.Tile = domain.Known(int32(12))
	if plan := PlanStoppedExpeditionReturn(trip, []domain.PawnID{"survivor"}); plan != nil {
		t.Fatal(plan)
	}
}
