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

func TestSlaughterFoodRequiresOptInAndProtectsFloor(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{{ID: "cow", Definition: "Cow", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)}})
	rows := []SlaughterFoodAnimal{{ID: "cow", Race: "Cow", MeatNutrition: domain.Known(15.0), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(10.0)}}
	if got := SlaughterFoodChannels(rows, animals, HerdPolicy{}); len(got) != 0 {
		t.Fatal("slaughter without opt-in", got)
	}
	herd := HerdPolicy{AllowSlaughter: true, PopulationMin: map[Resource]int64{"Cow": 1}}
	if got := SlaughterFoodChannels(rows, animals, herd); len(got) != 0 {
		t.Fatal("slaughter below floor", got)
	}
	herd.PopulationMin = nil
	channels := SlaughterFoodChannels(rows, animals, herd)
	if len(channels) != 1 {
		t.Fatal(channels)
	}
	plan := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: channels[0], Decision: FoodPlanOpen}}})
	if c := FoodSlaughterChoice(plan, animals, HerdPolicy{}); c.Method != "" {
		t.Fatal("dispatch without opt-in", c)
	}
	if c := FoodSlaughterChoice(plan, animals, herd); c.Method != domain.HusbandrySlaughter || c.Animal != "cow" {
		t.Fatal(c)
	}
}

func TestSlaughterRanksFeedEfficiencyThenReproduction(t *testing.T) {
	var animals []UpkeepAnimal
	var rows []SlaughterFoodAnimal
	for i, id := range []string{"slow", "fast", "inefficient"} {
		animals = append(animals, UpkeepAnimal{ID: PawnID(id), Definition: Resource(id), Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)})
		n, days := 10.0, 10.0
		if i == 1 {
			days = 2
		}
		if i == 2 {
			n = 5
			days = 1
		}
		rows = append(rows, SlaughterFoodAnimal{ID: PawnID(id), Race: Resource(id), MeatNutrition: domain.Known(n), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(days)})
	}
	channels := SlaughterFoodChannels(rows, domain.Known(animals), HerdPolicy{AllowSlaughter: true})
	if len(channels) != 1 || channels[0].ID != "slaughter:fast" {
		t.Fatal(channels)
	}
}
