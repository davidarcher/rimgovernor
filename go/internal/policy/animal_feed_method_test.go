package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func feedStock(id string, def Resource, holder PawnID, count int64, nutrition float64, eaters ...PawnID) FoodStock {
	return FoodStock{ID: id, DefName: def, Holder: domain.Known(holder), Count: domain.Known(count), Nutrition: domain.Known(nutrition), Eaters: eaters}
}

// feedGroup is one short race group of the given deficit.
func feedGroup(def Resource, deficit float64, animals ...PawnID) AnimalFeedGroup {
	return AnimalFeedGroup{Definition: def, Animals: animals, DeficitNutrition: deficit, TargetNutrition: deficit}
}

func TestAnimalFeedSelectsCheapestCoveringSharedStock(t *testing.T) {
	group := feedGroup("Muffalo", 10, "muffalo1", "muffalo2")
	stocks := []FoodStock{
		feedStock("hay", "Hay", "", 20, 40, "muffalo1", "muffalo2"),
		// Held stock is not shared and must be skipped.
		feedStock("private", "Kibble", "colonist1", 100, 1000, "muffalo1", "muffalo2"),
		// Does not cover every animal of the group.
		feedStock("partial", "Grass", "", 20, 40, "muffalo1"),
	}
	choice, err := SelectAnimalFeedMethod(group, stocks, map[Resource]int64{"Hay": 3}, testRaces)
	if err != nil {
		t.Fatal(err)
	}
	if choice.Reason != AnimalFeedSelected || choice.Resource != "Hay" {
		t.Fatalf("%+v", choice)
	}
	// deficit = 10, nutritionPerItem = 40/20 = 2 -> 5 items; have=3 -> target=8
	if choice.Target != 8 {
		t.Fatalf("target = %d", choice.Target)
	}
}

func TestAnimalFeedTieBreaksLowestDefNameThenID(t *testing.T) {
	stocks := []FoodStock{
		feedStock("z", "Kibble", "", 10, 20, "muffalo1"),
		feedStock("b", "Hay", "", 10, 20, "muffalo1"),
		feedStock("a", "Hay", "", 10, 20, "muffalo1"),
	}
	choice, err := SelectAnimalFeedMethod(feedGroup("Muffalo", 2, "muffalo1"), stocks, nil, testRaces)
	if err != nil || choice.Resource != "Hay" {
		t.Fatalf("%+v %v", choice, err)
	}
}

func TestAnimalFeedNoDeficitWhenGroupReserveMet(t *testing.T) {
	choice, err := SelectAnimalFeedMethod(feedGroup("Muffalo", 0, "muffalo1"), nil, nil, testRaces)
	if err != nil || choice.Reason != AnimalFeedNoDeficit {
		t.Fatalf("%+v %v", choice, err)
	}
}

var testRaces = AnimalRaceCatalog{Races: map[Resource]AnimalRace{
	"Muffalo": {Def: "Muffalo", FeedItems: []RaceFeedItem{{Def: "Kibble", Nutrition: 0.05}, {Def: "Pemmican", Nutrition: 0.5}}},
	"Chicken": {Def: "Chicken", FeedItems: []RaceFeedItem{{Def: "Kibble", Nutrition: 0.05}}},
	"Husky":   {Def: "Husky", FeedItems: []RaceFeedItem{{Def: "Kibble", Nutrition: 0.05}}},
	"Thrumbo": {Def: "Thrumbo"},
}}

func TestAnimalFeedProducesCheapestItemWhenNothingCovers(t *testing.T) {
	group := feedGroup("Muffalo", 4, "muffalo1")
	choice, err := SelectAnimalFeedMethod(group, nil, map[Resource]int64{"Kibble": 3}, testRaces)
	// 4 nutrition at 0.05 per kibble is 80 items above the 3 in stock.
	if err != nil || choice.Reason != AnimalFeedSelected || choice.Resource != "Kibble" || !choice.Produced || choice.Target != 83 {
		t.Fatalf("%+v %v", choice, err)
	}
	// A stock that does not cover the group is likewise produced.
	stocks := []FoodStock{feedStock("hay", "Hay", "", 10, 40, "other")}
	choice, err = SelectAnimalFeedMethod(group, stocks, nil, testRaces)
	if err != nil || choice.Reason != AnimalFeedSelected || choice.Resource != "Kibble" {
		t.Fatalf("%+v %v", choice, err)
	}
}

func TestAnimalFeedNoFeedWhenRaceEatsNothingProducible(t *testing.T) {
	group := feedGroup("Thrumbo", 4, "thrumbo1")
	choice, err := SelectAnimalFeedMethod(group, nil, nil, testRaces)
	if err != nil || choice.Reason != AnimalFeedNoFeed {
		t.Fatalf("%+v %v", choice, err)
	}
	if _, err = SelectAnimalFeedMethod(group, nil, nil, AnimalRaceCatalog{}); err == nil {
		t.Fatal("race missing from the catalog must fail loudly")
	}
}

func TestAnimalFeedExceedsBoundedPlanningLimit(t *testing.T) {
	stocks := []FoodStock{feedStock("hay", "Hay", "", 10, 40, "muffalo1")}
	choice, err := SelectAnimalFeedMethod(feedGroup("Muffalo", 1000000, "muffalo1"), stocks, nil, testRaces)
	if err != nil || choice.Reason != AnimalFeedExceedsBound {
		t.Fatalf("%+v %v", choice, err)
	}
}

func TestAnimalFeedInvalidGroupRejected(t *testing.T) {
	for _, group := range []AnimalFeedGroup{
		feedGroup("Muffalo", 4),
		feedGroup("", 4, "muffalo1"),
		feedGroup("Muffalo", -1, "muffalo1"),
	} {
		if _, err := SelectAnimalFeedMethod(group, nil, nil, testRaces); err == nil {
			t.Fatalf("invalid group accepted: %+v", group)
		}
	}
}

// The method carries the group's shared benches unchanged: the bill may only
// land on a bench every animal of the group reaches (#237).
func TestAnimalFeedCarriesGroupBenches(t *testing.T) {
	group := feedGroup("Husky", 2, "husky1", "husky2")
	group.ReachableBenches = []string{"Thing_ButcherSpot1", "Thing_ButcherSpot3"}
	choice, err := SelectAnimalFeedMethod(group, nil, nil, testRaces)
	if err != nil || choice.Reason != AnimalFeedSelected || !reflect.DeepEqual(choice.Benches, group.ReachableBenches) {
		t.Fatalf("%+v %v", choice, err)
	}
	group.ReachableBenches = []string{}
	choice, err = SelectAnimalFeedMethod(group, nil, nil, testRaces)
	if err != nil || choice.Reason != AnimalFeedSelected || choice.Benches == nil || len(choice.Benches) != 0 {
		t.Fatalf("%+v %v", choice, err)
	}
}

// With no shared bench, feed made anywhere still feeds the group once a
// stockpile accepting it sits inside every animal's area (Delivered); failing
// that the method names the footprint they share (#311).
func TestAnimalFeedDeliveryFallsBackToReachableStorage(t *testing.T) {
	kibble := []AnimalFeedStorage{{Zone: "Zone_3", Accepts: []string{"Hay", "Kibble"}}}
	hayOnly := []AnimalFeedStorage{{Zone: "Zone_4", Accepts: []string{"Hay"}}}
	cells := []domain.Cell{{X: 6, Z: 5}, {X: 6, Z: 6}}
	group := feedGroup("Husky", 2, "husky1", "husky2")
	group.ReachableStorage, group.StorageCandidates = [][]AnimalFeedStorage{kibble, kibble}, cells
	choice, err := SelectAnimalFeedMethod(group, nil, nil, testRaces)
	if err != nil || choice.Reason != AnimalFeedSelected || choice.Resource != "Kibble" || !choice.Delivered || len(choice.Benches) != 0 || !reflect.DeepEqual(choice.StorageCells, cells) {
		t.Fatalf("%+v %v", choice, err)
	}
	group.ReachableStorage = [][]AnimalFeedStorage{kibble, hayOnly}
	choice, err = SelectAnimalFeedMethod(group, nil, nil, testRaces)
	if err != nil || choice.Reason != AnimalFeedSelected || choice.Delivered {
		t.Fatalf("%+v %v", choice, err)
	}
}
