package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"testing"
)

func product(pawn, race string, nutrition, work, lead float64) AnimalProduct {
	return AnimalProduct{Pawn: pawn, Race: race, Active: domain.Known(true), Reachable: domain.Known(true), NutritionPerDay: domain.Known(nutrition), WorkPerDay: domain.Known(work), LeadDays: domain.Known(lead)}
}

func TestAnimalProductsAggregateNativeRatesAndReadiness(t *testing.T) {
	rows := []AnimalProduct{product("cow", "Cow", .9, 400, 0), product("yak", "Yak", .3, 200, 1), product("hen1", "Chicken", .25, 0, .5), product("hen2", "Chicken", .25, 0, 0)}
	channels := AnimalProductChannels(rows)
	if len(channels) != 3 {
		t.Fatal(channels)
	}
	for _, c := range channels {
		n, _ := c.NutritionPerDay.Value()
		w, _ := c.WorkPerDay.Value()
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
	for _, mode := range []string{"rate", "access", "active", "inactive", "invalid"} {
		t.Run(mode, func(t *testing.T) {
			a := product("cow", "Cow", .9, 400, 0)
			switch mode {
			case "rate":
				a.NutritionPerDay = domain.Unknown[float64]()
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
			v, known := channels[0].NutritionPerDay.Value()
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
			plan := FoodPlan{Portfolio: []FoodPlanEntry{entry}}
			if mode == "crop" {
				plan.Portfolio = append(plan.Portfolio, FoodPlanEntry{Channel: FoodChannel{Kind: FoodCrop, NutritionPerDay: domain.Known(1.0), WorkPerDay: domain.Known(1.0)}})
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
