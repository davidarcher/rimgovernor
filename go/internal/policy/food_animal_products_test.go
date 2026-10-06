package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func product(pawn, race string, nutrition, work, lead float64) AnimalProduct {
	return AnimalProduct{Pawn: pawn, Race: race, Active: domain.Known(true), Reachable: domain.Known(true), NutritionPerDay: domain.Known(nutrition), WorkPerDay: domain.Known(work), LeadDays: domain.Known(lead), FeedPerDay: domain.Known(0.0)}
}

func TestAnimalProductsAggregateNativeRatesAndReadiness(t *testing.T) {
	rows := []AnimalProduct{product("cow", "Cow", .9, 400, 0), product("yak", "Yak", .3, 200, 1), product("hen1", "Chicken", .25, 0, .5), product("hen2", "Chicken", .25, 0, 0)}
	channels := AnimalProductChannels(rows)
	if len(channels) != 3 {
		t.Fatal(channels)
	}
	for _, c := range channels {
		n, _ := c.Nutrition().PerDay.Value()
		w, _ := c.LaborPerDay.Value()
		l, _ := c.LeadDays.Value()
		switch c.ID {
		case "Cow":
			if n != .9 || w != 400 || l != 0 {
				t.Fatal(c)
			}
		case "Yak":
			if n != .3 || w != 200 || l != 1 {
				t.Fatal(c)
			}
		case "Chicken":
			if n != .5 || w != 0 || l != 0 {
				t.Fatal(c)
			}
		default:
			t.Fatal(c)
		}
	}
}

func TestAnimalProductsDoNotInventMissingOrInactiveProduction(t *testing.T) {
	for _, mode := range []string{"rate", "feed", "access", "active", "inactive", "invalid"} {
		t.Run(mode, func(t *testing.T) {
			a := product("cow", "Cow", .9, 400, 0)
			switch mode {
			case "rate":
				a.NutritionPerDay = domain.Unknown[float64]()
			case "feed":
				a.FeedPerDay = domain.Unknown[float64]()
			case "access":
				a.Reachable = domain.Known(false)
			case "active":
				a.Active = domain.Unknown[bool]()
			case "inactive":
				a.Active = domain.Known(false)
			case "invalid":
				a.NutritionPerDay = domain.Known(-1.0)
			}
			channels := AnimalProductChannels([]AnimalProduct{a})
			if mode == "inactive" {
				if len(channels) != 0 {
					t.Fatal(channels)
				}
				return
			}
			v, known := channels[0].Nutrition().PerDay.Value()
			if mode == "invalid" {
				if !known || !math.IsNaN(v) {
					t.Fatal(channels)
				}
			} else if known {
				t.Fatal(channels)
			}
		})
	}
}

func TestFoodHerdFloorRequiresAdmittedEfficientProduction(t *testing.T) {
	for _, mode := range []string{"derived", "operator", "ceiling", "closed", "labor", "crop"} {
		t.Run(mode, func(t *testing.T) {
			c := AnimalProductChannels([]AnimalProduct{product("cow", "Cow", .9, 400, 0)})[0]
			entry := FoodPlanEntry{Channel: c, Decision: FoodPlanHold, DeliveredPerDay: .9}
			herd := HerdPolicy{PopulationMin: map[Resource]int64{}}
			want := int64(1)
			switch mode {
			case "operator":
				herd.PopulationMin["Cow"] = 4
				want = 4
			case "ceiling":
				herd.PopulationMax = map[Resource]int64{"Cow": 1}
			case "closed":
				entry.Decision = FoodPlanClose
				want = 0
			case "labor":
				entry.DeliveredPerDay = 0
				want = 0
			case "crop":
				want = 0
			}
			crop := FoodCandidate(CandidateCrop, "", domain.Known(1.0))
			crop.LaborPerDay = domain.Known(1.0)
			plan := FoodPlan{Portfolio: []FoodPlanEntry{entry}}
			if mode == "crop" {
				plan.Portfolio = append(plan.Portfolio, FoodPlanEntry{Channel: crop})
			}
			got := FoodHerdPolicy(herd, domain.Known(plan))
			if got.PopulationMin["Cow"] != want {
				t.Fatal(got)
			}
			if mode != "operator" && len(herd.PopulationMin) != 0 {
				t.Fatal("mutated operator policy")
			}
		})
	}
}

func TestAnimalProductRateIsNetOfFeed(t *testing.T) {
	cow, yak := product("cow", "Cow", 2, 400, 0), product("yak", "Yak", 1, 200, 0)
	cow.FeedPerDay, yak.FeedPerDay = domain.Known(.5), domain.Known(3.0)
	for _, c := range AnimalProductChannels([]AnimalProduct{cow, yak}) {
		n, _ := c.Nutrition().PerDay.Value()
		switch c.ID {
		case "Cow":
			if n != 1.5 {
				t.Fatal("net of feed", c)
			}
		case "Yak":
			if n != 0 {
				t.Fatal("feed above product is no yield", c)
			}
		}
	}
}

func TestWithFeedReadsTheUpkeepCensus(t *testing.T) {
	ghost := product("ghost", "Cow", 2, 0, 0)
	ghost.FeedPerDay = domain.Unknown[float64]()
	rows := WithFeed([]AnimalProduct{product("cow", "Cow", 2, 0, 0), ghost},
		domain.Known([]UpkeepAnimal{{ID: "cow", Herd: HerdFacts{FeedPerDay: domain.Known(.75)}}}))
	if f, ok := rows[0].FeedPerDay.Value(); !ok || f != .75 {
		t.Fatal(rows[0])
	}
	if _, ok := rows[1].FeedPerDay.Value(); ok {
		t.Fatal("an animal outside the census has no feed fact")
	}
}
