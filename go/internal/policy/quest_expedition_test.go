package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func expeditionFixture() (JoinerOffer, ExpeditionSite, RoundsFacts, FoodSupply) {
	offer, f := departureFixture(QuestFamilyBanditCamp, 1)
	offer.ScriptDef = "OpportunitySite_BanditCamp"
	rows, _ := f.QuestDeparturePawns.Value()
	for i := range rows {
		rows[i].CarryCapacity = domain.Known(35.0)
		rows[i].CarriedMass = domain.Known(5.0)
	}
	f.QuestDeparturePawns = domain.Known(rows)
	site := ExpeditionSite{ID: "site", Quest: offer.Quest, Tile: 10, Layer: domain.Known(int32(0)), ThreatPoints: domain.Known(20.0), TravelTicks: domain.Known(int64(domain.TicksPerDay)), Reachable: domain.Known(true), RoutePawnIDs: []domain.PawnID{"a", "b", "c"}, HoldTicks: domain.Known(int64(0))}
	food := FoodSupply{Complete: domain.Known(true), Consumers: []FoodConsumer{{ID: "a", NutritionPerDay: domain.Known(1.0)}, {ID: "b", NutritionPerDay: domain.Known(1.0)}, {ID: "c", NutritionPerDay: domain.Known(1.0)}, {ID: "home", NutritionPerDay: domain.Known(1.0)}}, Stocks: []FoodStock{{ID: "meal", DefName: "MealSurvivalPack", Count: domain.Known(int64(30)), Nutrition: domain.Known(30.0), Holder: domain.Known(PawnID("")), Eaters: []PawnID{"a", "b", "c", "home"}, Perishable: domain.Known(false), Forbidden: domain.Known(false), UnitMass: domain.Known(0.3)}}}
	return offer, site, f, food
}

func TestExpeditionFormationTravelArrivalDoNotSendReplacement(t *testing.T) {
	offer, site, f, food := expeditionFixture()
	f.AnimalUpkeep.Food = domain.Known(food)
	f.QuestSites = domain.Known([]WorldSite{{ID: site.ID, Tile: domain.Known(site.Tile), Layer: site.Layer, State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED, QuestIDs: []domain.QuestID{offer.Quest}, ThreatPoints: site.ThreatPoints, Reachable: site.Reachable, TravelTicks: site.TravelTicks, RoutePawnIDs: site.RoutePawnIDs}})
	f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
	if plan := SelectExpedition(f, DefaultRoundsPolicy()); plan.Departure == nil || plan.Reason != "" {
		t.Fatal(plan)
	}
	for _, trip := range []ExpeditionTrip{{Forming: true}, {Destination: domain.Known(site.Tile)}, {Tile: domain.Known(site.Tile)}} {
		f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{trip})
		if plan := SelectExpedition(f, DefaultRoundsPolicy()); !plan.Waiting || plan.Departure != nil {
			t.Fatal(plan)
		}
	}
	f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
	sites, _ := f.QuestSites.Value()
	sites[0].State = o.WorldSiteState_WORLD_SITE_STATE_MAP_LOADED
	f.QuestSites = domain.Known(sites)
	if plan := SelectExpedition(f, DefaultRoundsPolicy()); !plan.Waiting || plan.Departure != nil {
		t.Fatal(plan)
	}
	offer.State = "EndedSuccess"
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	if plan := SelectExpedition(f, DefaultRoundsPolicy()); plan.Quest != "" {
		t.Fatal(plan)
	}
}

func TestExpeditionFormsStrongCrewAndRoundTripFood(t *testing.T) {
	offer, site, f, food := expeditionFixture()
	plan := PlanExpedition(offer, site, f, domain.Known(food), DefaultRoundsPolicy())
	if plan.Reason != "" || plan.Departure == nil || len(plan.Departure.Crew()) != 1 || plan.Departure.DestinationTile() != 10 {
		t.Fatal(plan)
	}
	if cargo := plan.Departure.Cargo(); len(cargo) != 1 || cargo[0].Count != 3 {
		t.Fatal(cargo)
	}
}

func TestExpeditionRefusesWeakFarHungryOrBusy(t *testing.T) {
	for _, test := range []struct {
		name   string
		change func(*ExpeditionSite, *RoundsFacts, *FoodSupply)
		reason QuestSkipReason
	}{
		{"weak", func(s *ExpeditionSite, f *RoundsFacts, food *FoodSupply) { s.ThreatPoints = domain.Known(100.0) }, "home_defense"},
		{"far", func(s *ExpeditionSite, f *RoundsFacts, food *FoodSupply) {
			s.TravelTicks = domain.Known(int64(30 * domain.TicksPerDay))
		}, "food_capacity"},
		{"hungry", func(s *ExpeditionSite, f *RoundsFacts, food *FoodSupply) {
			food.Stocks[0].Count = domain.Known(int64(10))
			food.Stocks[0].Nutrition = domain.Known(10.0)
		}, "food_capacity"},
		{"busy", func(s *ExpeditionSite, f *RoundsFacts, food *FoodSupply) { f.QuestColonyCalm = domain.Known(false) }, "colony_busy"},
		{"unknown route", func(s *ExpeditionSite, f *RoundsFacts, food *FoodSupply) { s.TravelTicks = domain.Unknown[int64]() }, "route_unknown"},
		{"layer", func(s *ExpeditionSite, f *RoundsFacts, food *FoodSupply) { s.Layer = domain.Known(int32(1)) }, "ship_only"},
		{"mass", func(s *ExpeditionSite, f *RoundsFacts, food *FoodSupply) {
			food.Stocks[0].UnitMass = domain.Known(100.0)
		}, "food_capacity"},
		{"diet", func(s *ExpeditionSite, f *RoundsFacts, food *FoodSupply) { food.Stocks[0].Eaters = []PawnID{"home"} }, "food_capacity"},
	} {
		t.Run(test.name, func(t *testing.T) {
			offer, site, f, food := expeditionFixture()
			test.change(&site, &f, &food)
			plan := PlanExpedition(offer, site, f, domain.Known(food), DefaultRoundsPolicy())
			if plan.Reason != test.reason || plan.Departure != nil {
				t.Fatal(plan)
			}
		})
	}
}

func TestExpeditionFoodPreservesEachHomeDietAndRot(t *testing.T) {
	_, _, _, food := expeditionFixture()
	food.Stocks[0].Perishable = domain.Known(true)
	food.Stocks[0].RotTicks = domain.Known(int64(2 * domain.TicksPerDay))
	if _, reason := expeditionFood(food, []domain.PawnID{"a"}, 3, 3, 30); reason != "food_capacity" {
		t.Fatal(reason)
	}
	food.Stocks[0].Perishable = domain.Known(false)
	food.Consumers[3].NutritionPerDay = domain.Known(10.0)
	if _, reason := expeditionFood(food, []domain.PawnID{"a"}, 3, 3, 30); reason != "food_capacity" {
		t.Fatal(reason)
	}
}
