package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestReconcileHerdRemoval(t *testing.T) {
	for _, method := range []domain.HusbandryMethod{domain.HusbandryCancelRelease, domain.HusbandryCancelSlaughter} {
		t.Run(string(method), func(t *testing.T) {
			v := animalFixture(0)
			rows, _ := v.Animals.Value()
			rows[0].Training = []HusbandryTrainable{trainable("Obedience", true, false)}
			rows[0].Release = domain.Known(method == domain.HusbandryCancelRelease)
			rows[0].Slaughter = domain.Known(method == domain.HusbandryCancelSlaughter)
			v.Animals = domain.Known(rows)
			herd := HerdPolicy{AllowRelease: true, AllowSlaughter: true, PopulationMin: map[Resource]int64{"Muffalo": 1}, PopulationMax: map[Resource]int64{"Muffalo": 1}}
			// Reconstructed native facts, including after restart/save-load, must
			// choose cancellation without needing an earlier controller action.
			for range 2 {
				got := ReconcileHerdRemoval(v.Animals, herd, domain.Known(FoodPlan{}))
				if got.Method != method || got.Animal != "muffalo" {
					t.Fatal(got)
				}
			}
			rows[0].Release, rows[0].Slaughter = domain.Known(false), domain.Known(false)
			v.Animals = domain.Known(rows)
			if got := ReconcileHerdRemoval(v.Animals, herd, domain.Known(FoodPlan{})); got.Reason != HusbandryNoDeficit {
				t.Fatal(got)
			}
			wild := domain.Known([]UpkeepAnimal{wildAnimal("replacement", "Muffalo", true, false)})
			if got := SelectHusbandryMethod(v.Animals, wild, feedFine, herd, anyTamer); got.Method != domain.HusbandryTrain {
				t.Fatal(got)
			}
			rows[0].Training = nil
			if got := SelectHusbandryMethod(domain.Known(rows), wild, feedFine, herd, anyTamer); got.Method != "" {
				t.Fatal("unwanted replacement", got)
			}
			upkeep, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy())
			feed, fk := upkeep.Feed.Value()
			contain, ck := upkeep.Containment.Value()
			if err != nil || !fk || !ck || len(feed) != 1 || len(contain) != 1 {
				t.Fatal(upkeep, err)
			}
		})
	}
}

func TestPendingRemovalBudgetsAndPolicyChanges(t *testing.T) {
	rows := []UpkeepAnimal{
		{ID: "a", Definition: "Cow", Release: domain.Known(true), Slaughter: domain.Known(false)},
		{ID: "b", Definition: "Cow", Release: domain.Known(false), Slaughter: domain.Known(false)},
	}
	herd := HerdPolicy{AllowRelease: true, PopulationMax: map[Resource]int64{"Cow": 1}}
	food := domain.Known(FoodPlan{})
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
	herd.AllowRelease = false
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Method != domain.HusbandryCancelRelease {
		t.Fatal(got)
	}
	herd.AllowRelease = true
	rows[1].Release = domain.Known(true)
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Animal != "b" || got.Method != domain.HusbandryCancelRelease {
		t.Fatal(got)
	}
	rows[0].Release, rows[0].Slaughter = domain.Known(false), domain.Known(true)
	rows[1].Release = domain.Known(false)
	herd = HerdPolicy{AllowSlaughter: true, PopulationMin: map[Resource]int64{"Cow": 1}}
	food = domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: FoodChannel{Kind: FoodHunt, ID: "slaughter:a"}, Decision: FoodPlanOpen}}})
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, domain.Unknown[FoodPlan]()); got.Reason != HusbandryUnknown {
		t.Fatal(got)
	}
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, domain.Known(FoodPlan{})); got.Method != domain.HusbandryCancelSlaughter {
		t.Fatal(got)
	}
	herd.PopulationMin["Cow"] = 2
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Method != domain.HusbandryCancelSlaughter {
		t.Fatal(got)
	}
}

func TestFoodOfferRetainsPendingSlaughterWithoutDuplicate(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{{ID: "cow", Definition: "Cow", Release: domain.Known(false), Slaughter: domain.Known(true), SafeToSlaughter: domain.Known(false)}})
	rows := []SlaughterFoodAnimal{{ID: "cow", Race: "Cow", MeatNutrition: domain.Known(15.0), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(10.0)}}
	herd := HerdPolicy{AllowSlaughter: true}
	channels := SlaughterFoodChannels(rows, animals, herd)
	if len(channels) != 1 {
		t.Fatal(channels)
	}
	food := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: channels[0], Decision: FoodPlanOpen}}})
	if got := ReconcileHerdRemoval(animals, herd, food); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
	if got := FoodSlaughterChoice(food, animals, herd); got.Method != "" {
		t.Fatal("duplicate removal", got)
	}
}
