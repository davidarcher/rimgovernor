package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func foodFixture() FoodSupply {
	return FoodSupply{Complete: domain.Known(true), Consumers: []FoodConsumer{{ID: "a", NutritionPerDay: domain.Known(2.)}, {ID: "b", NutritionPerDay: domain.Known(1.)}}, Stocks: []FoodStock{{ID: "rice", Holder: domain.Known(PawnID("")), Nutrition: domain.Known(9.), Eaters: []PawnID{"a", "b"}, Perishable: domain.Known(true), RotTicks: domain.Known(int64(60000))}}}
}
func durableFood(id string, amount float64, holder PawnID, eaters ...PawnID) FoodStock {
	return FoodStock{ID: id, Holder: domain.Known(holder), Nutrition: domain.Known(amount), Eaters: eaters, Perishable: domain.Known(false)}
}
func TestFoodForecastExpiryInventoryDietAndCompetingDemand(t *testing.T) {
	for _, test := range []struct {
		name                            string
		change                          func(*FoodSupply)
		selected                        []PawnID
		runway, usable, risk, inventory float64
	}{
		{"rot", nil, nil, 1, 3, 6, 0},
		{"durable after rot", func(s *FoodSupply) { s.Stocks = append(s.Stocks, durableFood("pemmican", 6, "", "a", "b")) }, nil, 3, 9, 6, 0},
		{"private stock", func(s *FoodSupply) { s.Stocks = []FoodStock{durableFood("pack", 30, "a", "a")} }, nil, 0, 30, 0, 30},
		{"carried stock", func(s *FoodSupply) {
			s.Stocks = append(s.Stocks, durableFood("pack", 4, "a", "a"), durableFood("carry", 2, "b", "b"))
		}, nil, 3, 9, 6, 6},
		{"diet access", func(s *FoodSupply) {
			s.Stocks = []FoodStock{durableFood("diet", 6, "", "a"), durableFood("reachable", 2, "", "b")}
		}, nil, 2, 8, 0, 0},
		{"animal competition", func(s *FoodSupply) { s.Stocks = []FoodStock{durableFood("shared", 9, "", "a", "b")} }, []PawnID{"a"}, 3, 6, 0, 0},
		{"empty stock", func(s *FoodSupply) { s.Stocks = nil }, nil, 0, 0, 0, 0},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := foodFixture()
			if test.change != nil {
				test.change(&s)
			}
			got, err := ForecastFood(s, test.selected)
			if err != nil {
				t.Fatal(err)
			}
			if days, known := got.RunwayDays.Value(); !known || days != test.runway || got.UsableNutrition != test.usable || got.AtRiskNutrition != test.risk || got.InventoryNutrition != test.inventory {
				t.Fatal(got)
			}
			for i, j := 0, len(s.Stocks)-1; i < j; i, j = i+1, j-1 {
				s.Stocks[i], s.Stocks[j] = s.Stocks[j], s.Stocks[i]
			}
			s.Consumers[0], s.Consumers[1] = s.Consumers[1], s.Consumers[0]
			other, err := ForecastFood(s, test.selected)
			if err != nil || !reflect.DeepEqual(got, other) {
				t.Fatal("input order changed forecast", other, err)
			}
		})
	}
}
func TestFoodForecastUnknownAndInvalidInputsNeverCertifyRunway(t *testing.T) {
	for _, test := range []struct {
		name   string
		change func(*FoodSupply)
	}{
		{"incomplete", func(s *FoodSupply) { s.Complete = domain.Known(false) }},
		{"unknown completeness", func(s *FoodSupply) { s.Complete = domain.Unknown[bool]() }},
		{"duplicate stock", func(s *FoodSupply) { s.Stocks = append(s.Stocks, s.Stocks[0]) }},
		{"duplicate consumer", func(s *FoodSupply) { s.Consumers = append(s.Consumers, s.Consumers[0]) }},
		{"rot unknown", func(s *FoodSupply) { s.Stocks[0].RotTicks = domain.Unknown[int64]() }},
		{"rot negative", func(s *FoodSupply) { s.Stocks[0].RotTicks = domain.Known(int64(-1)) }},
		{"ownership unknown", func(s *FoodSupply) { s.Stocks[0].Holder = domain.Unknown[PawnID]() }},
		{"holder missing", func(s *FoodSupply) { s.Stocks[0].Holder = domain.Known(PawnID("missing")) }},
		{"inventory shared", func(s *FoodSupply) { s.Stocks[0].Holder = domain.Known(PawnID("a")) }},
		{"no eligible eaters", func(s *FoodSupply) { s.Stocks[0].Eaters = nil }},
		{"duplicate eater", func(s *FoodSupply) { s.Stocks[0].Eaters = []PawnID{"a", "a"} }},
		{"unknown eater", func(s *FoodSupply) { s.Stocks[0].Eaters = []PawnID{"missing"} }},
		{"negative demand", func(s *FoodSupply) { s.Consumers[0].NutritionPerDay = domain.Known(-1.) }},
		{"nan", func(s *FoodSupply) { s.Stocks[0].Nutrition = domain.Known(math.NaN()) }},
		{"infinity", func(s *FoodSupply) { s.Stocks[0].Nutrition = domain.Known(math.Inf(1)) }},
		{"overflow demand", func(s *FoodSupply) {
			for i := range s.Consumers {
				s.Consumers[i].NutritionPerDay = domain.Known(math.MaxFloat64)
			}
		}},
		{"unknown perishability", func(s *FoodSupply) { s.Stocks[0].Perishable = domain.Unknown[bool]() }},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := foodFixture()
			test.change(&s)
			got, err := ForecastFood(s, nil)
			if err == nil {
				t.Fatal("invalid food accepted", got)
			}
			if _, known := got.RunwayDays.Value(); known {
				t.Fatal("failure published runway")
			}
		})
	}
	for _, selection := range [][]PawnID{{"missing"}, {"a", "a"}} {
		if _, err := ForecastFood(foodFixture(), selection); err == nil {
			t.Fatal("invalid selection accepted")
		}
	}
	got, err := ForecastFood(foodFixture(), []PawnID{})
	if err != nil {
		t.Fatal(err)
	}
	if _, known := got.RunwayDays.Value(); known {
		t.Fatal("empty selection recovered food")
	}
}
