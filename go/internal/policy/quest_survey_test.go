package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func surveyFixture() (JoinerOffer, ExpeditionSite, RoundsFacts, FoodSupply, WorldSite) {
	offer, site, f, food := expeditionFixture()
	offer.ScriptDef = "SurveySite"
	offer.ThreatPoints = domain.Known(20.0)
	scanner := QuestSurveyScanner{SiteID: site.ID, DurationTicks: domain.Known(int64(15 * domain.TicksPerDay)), Complete: domain.Known(false)}
	offer.Objectives = []QuestObjective{{SurveyScanner: domain.Known(scanner)}}
	food.Stocks[0].Count = domain.Known(int64(100))
	food.Stocks[0].Nutrition = domain.Known(100.0)
	world := WorldSite{ID: site.ID, State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED, Tile: domain.Known(site.Tile), Layer: site.Layer, QuestIDs: []domain.QuestID{offer.Quest}, ThreatPoints: site.ThreatPoints, TravelTicks: site.TravelTicks, Reachable: site.Reachable, RoutePawnIDs: site.RoutePawnIDs}
	world.Security = domain.Known(SiteSecurity{Known: domain.Known(true), InitialPoints: site.ThreatPoints, PendingRaidPoints: domain.Known(0.0), TrapCount: domain.Known(int32(0)), ActiveThreat: domain.Known(false), DormantThreat: domain.Known(false)})
	f.QuestSites = domain.Known([]WorldSite{world})
	f.AnimalUpkeep.Food = domain.Known(food)
	return offer, site, f, food, world
}

func TestSurveySharedSelectorPricesHoldAndDefersLoadedSiteReturn(t *testing.T) {
	offer, _, f, _, world := surveyFixture()
	offer.Profile = domain.Known(QuestFamilyForRoot("SurveySite"))
	world.State = o.WorldSiteState_WORLD_SITE_STATE_SPAWNED
	f.QuestSites = domain.Known([]WorldSite{world})
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
	if plan := SelectExpedition(f, DefaultRoundsPolicy()); plan.Departure == nil || plan.Departure.Cargo()[0].Count != 18 || plan.Reason != "" {
		t.Fatal(plan)
	}
	if reason := ExpeditionAdmission(offer, f); reason != "" {
		t.Fatal(reason)
	}
	offer.State = "NotYetAccepted"
	offer.CanAccept = true
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	if accept, reason := questDecision(offer, f); !accept || reason != "" {
		t.Fatal(accept, reason)
	}
	offer.State = "Ongoing"
	scanner, _ := offer.Objectives[0].SurveyScanner.Value()
	scanner.Alive = domain.Known(true)
	scanner.EndTick = domain.Known(int64(15 * domain.TicksPerDay))
	offer.Objectives[0].SurveyScanner = domain.Known(scanner)
	f.QuestObservedTick = domain.Known(domain.Tick(1))
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	world.State = o.WorldSiteState_WORLD_SITE_STATE_MAP_LOADED
	world.Extraction = domain.Known(SiteExtraction{Crew: []domain.PawnID{"a"}, InventoryFoodDays: domain.Known(20.0), HomeRoutes: []SiteHomeRoute{{Reachable: domain.Known(true), TravelTicks: domain.Known(int64(domain.TicksPerDay))}}})
	f.QuestSites = domain.Known([]WorldSite{world})
	if plan := SelectExpedition(f, DefaultRoundsPolicy()); !plan.Waiting || plan.Departure != nil || plan.Reason != "" {
		t.Fatal(plan)
	}
}

func TestSurveyFundsFullNativeDurationAndHealthyRelief(t *testing.T) {
	offer, site, f, food, _ := surveyFixture()
	plan := PlanSurvey(offer, site, f, domain.Known(food), DefaultRoundsPolicy())
	if plan.Reason != "" || plan.Departure == nil || plan.Departure.Cargo()[0].Count != 18 {
		t.Fatal(plan)
	}
	for _, days := range []int64{10, 15} {
		scanner, _ := offer.Objectives[0].SurveyScanner.Value()
		scanner.DurationTicks = domain.Known(days * int64(domain.TicksPerDay))
		offer.Objectives[0].SurveyScanner = domain.Known(scanner)
		if plan := PlanSurvey(offer, site, f, domain.Known(food), DefaultRoundsPolicy()); plan.Departure == nil || plan.Departure.Cargo()[0].Count != uint64(days+3) {
			t.Fatal(days, plan)
		}
	}
	f.QuestSparePawns = domain.Known([]PawnID{"a"})
	if plan := PlanSurvey(offer, site, f, domain.Known(food), DefaultRoundsPolicy()); plan.Reason != "relief_capacity" || plan.Departure != nil {
		t.Fatal(plan)
	}
}

func TestSurveyRejectsForcedRaidAndFoodOrReliefShortage(t *testing.T) {
	offer, site, f, food, _ := surveyFixture()
	food.Stocks[0].Count = domain.Known(int64(30))
	food.Stocks[0].Nutrition = domain.Known(30.0)
	if plan := PlanSurvey(offer, site, f, domain.Known(food), DefaultRoundsPolicy()); plan.Reason != "relief_capacity" {
		t.Fatal(plan)
	}
	offer.ThreatPoints = domain.Known(500.0)
	if plan := PlanSurvey(offer, site, f, domain.Known(food), DefaultRoundsPolicy()); plan.Departure != nil || plan.Reason == "" {
		t.Fatal(plan)
	}
	offer.ThreatPoints = domain.Known(20.0)
	rows, _ := f.QuestDeparturePawns.Value()
	rows[1].HealthyAdult = domain.Known(false)
	rows[2].HealthyAdult = domain.Known(false)
	f.QuestDeparturePawns = domain.Known(rows)
	food.Stocks[0].Count = domain.Known(int64(100))
	food.Stocks[0].Nutrition = domain.Known(100.0)
	if plan := PlanSurvey(offer, site, f, domain.Known(food), DefaultRoundsPolicy()); plan.Reason != "relief_capacity" {
		t.Fatal(plan)
	}
}

func TestSurveyWaitsForNativeCompletionAndReturnsAfterScannerDestroyed(t *testing.T) {
	offer, _, f, food, world := surveyFixture()
	scanner, _ := offer.Objectives[0].SurveyScanner.Value()
	scanner.Alive = domain.Known(true)
	scanner.EndTick = domain.Known(int64(15 * domain.TicksPerDay))
	offer.Objectives[0].SurveyScanner = domain.Known(scanner)
	f.QuestObservedTick = domain.Known(domain.Tick(1))
	world.Extraction = domain.Known(SiteExtraction{Crew: []domain.PawnID{"a"}, InventoryFoodDays: domain.Known(20.0), HomeRoutes: []SiteHomeRoute{{Reachable: domain.Known(true), TravelTicks: domain.Known(int64(domain.TicksPerDay))}}})
	f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
	if plan := SurveyWork(offer, world, f, domain.Known(food), DefaultRoundsPolicy()); !plan.Waiting || plan.Return || plan.Departure != nil {
		t.Fatal(plan)
	}
	f.QuestObservedTick = domain.Known(domain.Tick(16 * domain.TicksPerDay))
	if plan := SurveyWork(offer, world, f, domain.Known(food), DefaultRoundsPolicy()); plan.Reason != "survey_completion_pending" || plan.Return {
		t.Fatal(plan)
	}
	scanner.Complete = domain.Known(true)
	offer.Objectives[0].SurveyScanner = domain.Known(scanner)
	if plan := SurveyWork(offer, world, f, domain.Known(food), DefaultRoundsPolicy()); !plan.Return || plan.Failed {
		t.Fatal(plan)
	}
	scanner.Alive = domain.Known(false)
	offer.Objectives[0].SurveyScanner = domain.Known(scanner)
	if plan := SurveyWork(offer, world, f, domain.Known(food), DefaultRoundsPolicy()); !plan.Return || !plan.Failed || plan.Reason != "scanner_destroyed" || plan.Departure != nil {
		t.Fatal(plan)
	}
}

func TestSurveyDispatchesSuppliedReliefWithoutDuplicatingJourney(t *testing.T) {
	offer, _, f, food, world := surveyFixture()
	scanner, _ := offer.Objectives[0].SurveyScanner.Value()
	scanner.Alive = domain.Known(true)
	scanner.EndTick = domain.Known(int64(15 * domain.TicksPerDay))
	offer.Objectives[0].SurveyScanner = domain.Known(scanner)
	f.QuestObservedTick = domain.Known(domain.Tick(1))
	f.QuestSparePawns = domain.Known([]PawnID{"b", "c"})
	world.Extraction = domain.Known(SiteExtraction{Crew: []domain.PawnID{"a"}, InventoryFoodDays: domain.Known(1.0), HomeRoutes: []SiteHomeRoute{{Reachable: domain.Known(true), TravelTicks: domain.Known(int64(domain.TicksPerDay))}}})
	f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
	if plan := SurveyWork(offer, world, f, domain.Known(food), DefaultRoundsPolicy()); plan.Departure == nil || plan.Departure.Crew()[0] == "a" || plan.Reason != "" {
		t.Fatal(plan)
	}
	f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{{Destination: world.Tile}})
	if plan := SurveyWork(offer, world, f, domain.Known(food), DefaultRoundsPolicy()); plan.Departure != nil || !plan.Waiting {
		t.Fatal(plan)
	}
}
