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
			herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 1}, PopulationMax: map[Resource]int64{"Muffalo": 1}}
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

// cows is a herd of one bull and n cows, none designated.
func cows(n int) []UpkeepAnimal {
	rows := []UpkeepAnimal{{ID: "bull", Definition: "Cow", Gender: "Male", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true), SafeToRelease: domain.Known(true)}}
	for i := range n {
		rows = append(rows, UpkeepAnimal{ID: PawnID("cow" + string(rune('a'+i))), Definition: "Cow", Gender: "Female", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true), SafeToRelease: domain.Known(true)})
	}
	return rows
}

func TestPendingRemovalBudgetsAndPolicyChanges(t *testing.T) {
	rows := cows(3)
	rows[1].Release = domain.Known(true)
	herd := HerdPolicy{PopulationMax: map[Resource]int64{"Cow": 3}}
	food := domain.Known(FoodPlan{})
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
	herd.PopulationMax["Cow"] = 4
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Method != domain.HusbandryCancelRelease {
		t.Fatal("no surplus left", got)
	}
	herd.PopulationMax["Cow"] = 3
	rows[2].Release = domain.Known(true)
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Animal != "cowb" || got.Method != domain.HusbandryCancelRelease {
		t.Fatal("second release breaks the pair", got)
	}
	rows[1].Release, rows[1].Slaughter = domain.Known(false), domain.Known(true)
	rows[2].Release = domain.Known(false)
	herd = HerdPolicy{PopulationMax: map[Resource]int64{"Cow": 30}}
	food = domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: FoodChannel{Kind: FoodHunt, ID: "slaughter:cowa"}, Decision: FoodPlanOpen}}})
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, domain.Unknown[FoodPlan]()); got.Reason != HusbandryUnknown {
		t.Fatal(got)
	}
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, domain.Known(FoodPlan{})); got.Method != domain.HusbandryCancelSlaughter {
		t.Fatal(got)
	}
	herd.PopulationMin = map[Resource]int64{"Cow": 4}
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Method != domain.HusbandryCancelSlaughter {
		t.Fatal(got)
	}
}

func TestFoodOfferRetainsPendingSlaughterWithoutDuplicate(t *testing.T) {
	herdRows := cows(3)
	herdRows[1].Slaughter, herdRows[1].SafeToSlaughter = domain.Known(true), domain.Known(false)
	animals := domain.Known(herdRows)
	rows := []SlaughterFoodAnimal{{ID: "cowa", Race: "Cow", MeatNutrition: domain.Known(15.0), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(10.0)}}
	herd := HerdPolicy{}
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
