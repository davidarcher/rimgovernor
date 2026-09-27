package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestHayOnlyForNegativeSeasonalGrazingBalance(t *testing.T) {
	for _, tc := range []struct {
		name                       string
		days, pasture, stock, want float64
	}{
		{"winter", 10, 0, 2, 18}, {"balanced", 10, 2, 0, 0}, {"surplus", 10, 3, 0, 0}, {"no-gap", 0, 0, 0, 0}, {"stored", 10, 0, 20, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			pens := domain.Known([]PenGrazing{{ID: "pen", DemandPerDay: domain.Known(2.0), PasturePerDay: domain.Known(tc.pasture), StoredNutrition: domain.Known(tc.stock)}})
			need := HayNutritionNeed(pens, domain.Known(tc.days))
			n, k := need.Value()
			if !k || n != tc.want {
				t.Fatal(need)
			}
			site := farmSiteFixture()
			crop := site.Crop
			crop.Name = "Plant_Haygrass"
			crop.Available = domain.Known(true)
			crop.Edible = domain.Known(false)
			plan, ok := PlanHayField(need, crop, CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(30.0)}, site)
			if ok != (tc.want > 0) {
				t.Fatal(plan, ok)
			}
			if ok && len(CropChannels([]FoodField{{ID: "hay", Plan: plan}})) != 0 {
				t.Fatal("hay counted as human nutrition")
			}
		})
	}
	if _, known := HayNutritionNeed(domain.Unknown[[]PenGrazing](), domain.Known(10.0)).Value(); known {
		t.Fatal("unknown pasture became a deficit")
	}
}

func TestSlaughterFoodProtectsFloorAndBreedingPair(t *testing.T) {
	rows := []SlaughterFoodAnimal{{ID: "cowa", Race: "Cow", MeatNutrition: domain.Known(15.0), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(10.0)}}
	if got := SlaughterFoodChannels(rows, domain.Known(cows(2)), HerdPolicy{}); len(got) != 0 {
		t.Fatal("slaughter below the breeding pair", got)
	}
	animals := domain.Known(cows(3))
	if got := SlaughterFoodChannels(rows, animals, HerdPolicy{PopulationMin: map[Resource]int64{"Cow": 4}}); len(got) != 0 {
		t.Fatal("slaughter below floor", got)
	}
	channels := SlaughterFoodChannels(rows, animals, HerdPolicy{})
	if len(channels) != 1 {
		t.Fatal(channels)
	}
	plan := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: channels[0], Decision: FoodPlanOpen}}})
	if c := FoodSlaughterChoice(plan, animals, HerdPolicy{}); c.Method != domain.HusbandrySlaughter || c.Animal != "cowa" {
		t.Fatal(c)
	}
}

func TestSlaughterRanksFeedEfficiencyThenReproduction(t *testing.T) {
	var animals []UpkeepAnimal
	var rows []SlaughterFoodAnimal
	for i, race := range []string{"slow", "fast", "inefficient"} {
		for _, a := range cows(3) {
			a.ID, a.Definition = PawnID(race+"-"+string(a.ID)), Resource(race)
			animals = append(animals, a)
		}
		n, days := 10.0, 10.0
		if i == 1 {
			days = 2
		}
		if i == 2 {
			n = 5
			days = 1
		}
		rows = append(rows, SlaughterFoodAnimal{ID: PawnID(race + "-cowa"), Race: Resource(race), MeatNutrition: domain.Known(n), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(days)})
	}
	channels := SlaughterFoodChannels(rows, domain.Known(animals), HerdPolicy{})
	if len(channels) != 1 || channels[0].ID != "slaughter:fast-cowa" {
		t.Fatal(channels)
	}
}
