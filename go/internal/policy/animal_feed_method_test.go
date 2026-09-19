package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func feedStock(id string, def Resource, holder PawnID, count int64, nutrition float64, eaters ...PawnID) FoodStock {
	return FoodStock{ID: id, DefName: def, Holder: domain.Known(holder), Count: domain.Known(count), Nutrition: domain.Known(nutrition), Eaters: eaters}
}

func TestAnimalFeedSelectsCheapestCoveringSharedStock(t *testing.T) {
	targets := []AnimalFeedTarget{
		{ID: "muffalo1", Definition: "Muffalo", Nutrition: 6},
		{ID: "muffalo2", Definition: "Muffalo", Nutrition: 4},
	}
	stocks := []FoodStock{
		feedStock("hay", "Hay", "", 20, 40, "muffalo1", "muffalo2"),
		// Held stock is not shared and must be skipped.
		feedStock("private", "Kibble", "colonist1", 100, 1000, "muffalo1", "muffalo2"),
		// Does not cover every deficit animal.
		feedStock("partial", "Grass", "", 20, 40, "muffalo1"),
	}
	choice, err := SelectAnimalFeedMethod(targets, stocks, map[Resource]int64{"Hay": 3}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if choice.Reason != AnimalFeedSelected || choice.Resource != "Hay" {
		t.Fatalf("%+v", choice)
	}
	// missing = 10, nutritionPerItem = 40/20 = 2 -> 5 items; have=3 -> target=8
	if choice.Target != 8 {
		t.Fatalf("target = %d", choice.Target)
	}
}

func TestAnimalFeedTieBreaksLowestDefNameThenID(t *testing.T) {
	targets := []AnimalFeedTarget{{ID: "muffalo1", Definition: "Muffalo", Nutrition: 2}}
	stocks := []FoodStock{
		feedStock("z", "Kibble", "", 10, 20, "muffalo1"),
		feedStock("b", "Hay", "", 10, 20, "muffalo1"),
		feedStock("a", "Hay", "", 10, 20, "muffalo1"),
	}
	choice, err := SelectAnimalFeedMethod(targets, stocks, nil, nil)
	if err != nil || choice.Resource != "Hay" {
		t.Fatalf("%+v %v", choice, err)
	}
}

func TestAnimalFeedIgnoresOtherRaceGroups(t *testing.T) {
	targets := []AnimalFeedTarget{
		{ID: "muffalo1", Definition: "Muffalo", Nutrition: 4},
		{ID: "chicken1", Definition: "Chicken", Nutrition: 100},
	}
	stocks := []FoodStock{feedStock("hay", "Hay", "", 10, 40, "muffalo1", "chicken1")}
	choice, err := SelectAnimalFeedMethod(targets, stocks, nil, nil)
	if err != nil || choice.Reason != AnimalFeedSelected {
		t.Fatalf("%+v %v", choice, err)
	}
	// Only muffalo1's 4 missing nutrition should count, not chicken1's 100.
	if choice.Target > 2 {
		t.Fatalf("target = %d, chicken deficit leaked into muffalo selection", choice.Target)
	}
}

func TestAnimalFeedNoDeficitWhenNoTargets(t *testing.T) {
	choice, err := SelectAnimalFeedMethod(nil, nil, nil, nil)
	if err != nil || choice.Reason != AnimalFeedNoDeficit {
		t.Fatalf("%+v %v", choice, err)
	}
}

func TestAnimalFeedFallsBackToKibbleWhenNothingCovers(t *testing.T) {
	targets := []AnimalFeedTarget{{ID: "muffalo1", Definition: "Muffalo", Nutrition: 4}}
	choice, err := SelectAnimalFeedMethod(targets, nil, map[Resource]int64{"Kibble": 3}, nil)
	// 4 nutrition at 0.05 per kibble is 80 items above the 3 in stock.
	if err != nil || choice.Reason != AnimalFeedSelected || choice.Resource != AnimalFeedFallbackResource || choice.Target != 83 {
		t.Fatalf("%+v %v", choice, err)
	}
	// A stock that doesn't cover the deficit animal is likewise kibble.
	stocks := []FoodStock{feedStock("hay", "Hay", "", 10, 40, "other")}
	choice, err = SelectAnimalFeedMethod(targets, stocks, nil, nil)
	if err != nil || choice.Reason != AnimalFeedSelected || choice.Resource != AnimalFeedFallbackResource {
		t.Fatalf("%+v %v", choice, err)
	}
	// Unless the player stopped kibble spending.
	choice, err = SelectAnimalFeedMethod(targets, stocks, nil, []Resource{"Kibble"})
	if err != nil || choice.Reason != AnimalFeedRestricted {
		t.Fatalf("%+v %v", choice, err)
	}
}

func TestAnimalFeedRestrictedByStoppedResource(t *testing.T) {
	targets := []AnimalFeedTarget{{ID: "muffalo1", Definition: "Muffalo", Nutrition: 4}}
	stocks := []FoodStock{feedStock("hay", "Hay", "", 10, 40, "muffalo1")}
	choice, err := SelectAnimalFeedMethod(targets, stocks, nil, []Resource{"Hay"})
	if err != nil || choice.Reason != AnimalFeedRestricted {
		t.Fatalf("%+v %v", choice, err)
	}
}

func TestAnimalFeedExceedsBoundedPlanningLimit(t *testing.T) {
	targets := []AnimalFeedTarget{{ID: "muffalo1", Definition: "Muffalo", Nutrition: 1000000}}
	stocks := []FoodStock{feedStock("hay", "Hay", "", 10, 40, "muffalo1")}
	choice, err := SelectAnimalFeedMethod(targets, stocks, nil, nil)
	if err != nil || choice.Reason != AnimalFeedExceedsBound {
		t.Fatalf("%+v %v", choice, err)
	}
}

func TestAnimalFeedInvalidInputsRejected(t *testing.T) {
	badTarget := []AnimalFeedTarget{{ID: "", Definition: "Muffalo", Nutrition: 4}}
	if _, err := SelectAnimalFeedMethod(badTarget, nil, nil, nil); err == nil {
		t.Fatal("empty animal id accepted")
	}
	nanTarget := []AnimalFeedTarget{{ID: "muffalo1", Definition: "Muffalo", Nutrition: -1}}
	stocks := []FoodStock{feedStock("hay", "Hay", "", 10, 40, "muffalo1")}
	if _, err := SelectAnimalFeedMethod(nanTarget, stocks, nil, nil); err == nil {
		t.Fatal("negative nutrition target accepted")
	}
	validTargets := []AnimalFeedTarget{{ID: "muffalo1", Definition: "Muffalo", Nutrition: 4}}
	badStop := []Resource{""}
	if _, err := SelectAnimalFeedMethod(validTargets, stocks, nil, badStop); err == nil {
		t.Fatal("empty stopped resource accepted")
	}
}

// The bill may only land on a bench every covered animal can reach, so the
// method carries the sorted intersection of the deficit race's reachable
// benches; another race's benches never widen it (#237).
func TestAnimalFeedCarriesBenchesEveryCoveredAnimalReaches(t *testing.T) {
	targets := []AnimalFeedTarget{
		{ID: "husky1", Definition: "Husky", Nutrition: 2, ReachableBenches: []string{"Thing_ButcherSpot2", "Thing_ButcherSpot1", "Thing_ButcherSpot3"}},
		{ID: "husky2", Definition: "Husky", Nutrition: 2, ReachableBenches: []string{"Thing_ButcherSpot3", "Thing_ButcherSpot1"}},
		{ID: "muffalo1", Definition: "Muffalo", Nutrition: 2, ReachableBenches: []string{"Thing_ButcherSpot9"}},
	}
	choice, err := SelectAnimalFeedMethod(targets, nil, nil, nil)
	if err != nil || choice.Reason != AnimalFeedSelected {
		t.Fatalf("%+v %v", choice, err)
	}
	if len(choice.Benches) != 2 || choice.Benches[0] != "Thing_ButcherSpot1" || choice.Benches[1] != "Thing_ButcherSpot3" {
		t.Fatalf("benches = %v", choice.Benches)
	}
	confined := []AnimalFeedTarget{{ID: "husky1", Definition: "Husky", Nutrition: 2, ReachableBenches: []string{}}}
	choice, err = SelectAnimalFeedMethod(confined, nil, nil, nil)
	if err != nil || choice.Reason != AnimalFeedSelected || choice.Benches == nil || len(choice.Benches) != 0 {
		t.Fatalf("%+v %v", choice, err)
	}
	bad := []AnimalFeedTarget{{ID: "husky1", Definition: "Husky", Nutrition: 2, ReachableBenches: []string{""}}}
	if _, err = SelectAnimalFeedMethod(bad, nil, nil, nil); err == nil {
		t.Fatal("blank bench id accepted")
	}
}
