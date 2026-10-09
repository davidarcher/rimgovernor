package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
			crop := CropChoice{Name: "Plant_Haygrass", Harvests: domain.Known(HayResource), Available: domain.Known(true), Edible: domain.Known(false), GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(0.5)}
			plan, ok := PlanHayField(need, crop, CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(30.0), OutdoorsDark: domain.Known(false)})
			if ok != (tc.want > 0) || ok && plan.Needed != int(tc.want*2) {
				t.Fatal(plan, ok)
			}
			if ok && len(CropChannels([]FoodField{{ID: "hay", Plan: plan}}, CropKitchen{}, CropSeason{})) != 0 {
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
	// A one-shot candidate: the meat is the stock cap, the slaughter work upfront.
	one := channels[0]
	if cap, _ := one.Yields[0].StockCap.Value(); cap != 15 || one.Kind != CandidateSlaughter {
		t.Fatal(one)
	}
	if up, _ := one.UpfrontCost.LaborTicks.Value(); up != 180 {
		t.Fatal(one)
	}
	if _, steady := one.Yields[0].PerDay.Value(); steady {
		t.Fatal("a slaughter has no rate", one)
	}
	if lead, _ := one.LeadDays.Value(); lead != 0 {
		t.Fatal(one)
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

// safeSlaughter is a fully known fact set: all clear when ok, else downed.
func safeSlaughter(ok bool) SlaughterFacts {
	f := func(v bool) domain.Fact[bool] { return domain.Known(v) }
	return SlaughterFacts{Downed: f(!ok), InMentalState: f(false), Pregnant: f(false), Mastered: f(false), ColonistBonded: f(false), Designatable: f(true)}
}

// slaughterRefused is a clear animal only the slaughter designator refuses, so
// removal falls through to release.
func slaughterRefused() SlaughterFacts {
	f := safeSlaughter(true)
	f.Designatable = domain.Known(false)
	return f
}

func herdSlaughterFacts(slaughter bool) SlaughterFacts {
	if slaughter {
		return safeSlaughter(true)
	}
	return slaughterRefused()
}

// Release is protected by Go policy, not native: the slaughter protections,
// a standing designation or a refusing release designator each bar it.
func TestReleaseAllowedExclusions(t *testing.T) {
	ok := UpkeepAnimal{SlaughterFacts: safeSlaughter(true), Release: domain.Known(false), Slaughter: domain.Known(false), SafeToRelease: domain.Known(true)}
	if v, k := ok.ReleaseAllowed().Value(); !k || !v {
		t.Fatal("clear animal not releasable")
	}
	for name, mutate := range map[string]func(*UpkeepAnimal){
		"downed":     func(a *UpkeepAnimal) { a.SlaughterFacts.Downed = domain.Known(true) },
		"bonded":     func(a *UpkeepAnimal) { a.SlaughterFacts.ColonistBonded = domain.Known(true) },
		"mastered":   func(a *UpkeepAnimal) { a.SlaughterFacts.Mastered = domain.Known(true) },
		"pregnant":   func(a *UpkeepAnimal) { a.SlaughterFacts.Pregnant = domain.Known(true) },
		"slaughter":  func(a *UpkeepAnimal) { a.Slaughter = domain.Known(true) },
		"designator": func(a *UpkeepAnimal) { a.SafeToRelease = domain.Known(false) },
	} {
		a := ok
		mutate(&a)
		if v, k := a.ReleaseAllowed().Value(); !k || v {
			t.Fatalf("%s animal releasable", name)
		}
	}
	a := ok
	a.Release = domain.Unknown[bool]()
	if _, k := a.ReleaseAllowed().Value(); k {
		t.Fatal("unknown designation read as known")
	}
}

func TestSafeToSlaughterExclusions(t *testing.T) {
	clear := func(mutate func(*UpkeepAnimal)) domain.Fact[bool] {
		a := UpkeepAnimal{SlaughterFacts: safeSlaughter(true), Release: domain.Known(false)}
		mutate(&a)
		return a.SafeToSlaughter()
	}
	for _, tc := range []struct {
		name   string
		mutate func(*UpkeepAnimal)
		want   bool
	}{
		{"clear", func(*UpkeepAnimal) {}, true},
		{"downed", func(a *UpkeepAnimal) { a.SlaughterFacts.Downed = domain.Known(true) }, false},
		{"mental state", func(a *UpkeepAnimal) { a.SlaughterFacts.InMentalState = domain.Known(true) }, false},
		{"pregnant", func(a *UpkeepAnimal) { a.SlaughterFacts.Pregnant = domain.Known(true) }, false},
		{"mastered", func(a *UpkeepAnimal) { a.SlaughterFacts.Mastered = domain.Known(true) }, false},
		{"colonist bond", func(a *UpkeepAnimal) { a.SlaughterFacts.ColonistBonded = domain.Known(true) }, false},
		{"release designated", func(a *UpkeepAnimal) { a.Release = domain.Known(true) }, false},
		{"designator refuses", func(a *UpkeepAnimal) { a.SlaughterFacts.Designatable = domain.Known(false) }, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if v, ok := clear(tc.mutate).Value(); !ok || v != tc.want {
				t.Fatalf("got %v %v", v, ok)
			}
		})
	}
	if _, ok := clear(func(a *UpkeepAnimal) { a.SlaughterFacts.Pregnant = domain.Unknown[bool]() }).Value(); ok {
		t.Fatal("unknown flag became safe")
	}
	if v, ok := clear(func(a *UpkeepAnimal) {
		a.SlaughterFacts.Pregnant = domain.Unknown[bool]()
		a.SlaughterFacts.Downed = domain.Known(true)
	}).Value(); !ok || v {
		t.Fatal("known blocker did not decide")
	}
}

func TestPlanHayFieldSizeFollowsNeedWithoutCap(t *testing.T) {
	crop := CropChoice{Name: "Plant_Haygrass", Harvests: domain.Known(HayResource), Available: domain.Known(true), Edible: domain.Known(false), GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(0.5)}
	plan, ok := PlanHayField(domain.Known(5000.0), crop, CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(30.0), OutdoorsDark: domain.Known(false)})
	if !ok || plan.Needed != 10000 {
		t.Fatal(plan, ok)
	}
}
