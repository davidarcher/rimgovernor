package policy

import (
	"fmt"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestStationFactsInputsKeyTracksTheChainsInputs(t *testing.T) {
	base := stationFactsFixture().InputsKey()
	if again := stationFactsFixture().InputsKey(); again != base {
		t.Fatal("the key is not stable")
	}
	for name, edit := range map[string]func(*StationFacts){
		"stock":     func(f *StationFacts) { f.Stock = domain.Known(map[Resource]int64{"Steel": 91}) },
		"colonists": func(f *StationFacts) { f.Colonists = domain.Known(int64(5)) },
		"built":     func(f *StationFacts) { f.Built = map[int32]bool{} },
		"plan": func(f *StationFacts) {
			f.Plan.Rooms = append(slices.Clone(f.Plan.Rooms), PlannedRoom{ID: 2, Role: PlannedKitchen, Region: region(3, 3)})
		},
		"unknown": func(f *StationFacts) { f.Orders = domain.Unknown[[]OrderSpec]() },
	} {
		f := stationFactsFixture()
		edit(&f)
		if f.InputsKey() == base {
			t.Errorf("%s did not change the key", name)
		}
	}
}

// BenchmarkAdviseStationsLarge is the station chain a layout review runs on
// the 250x250 baseline map for a 30-colonist plan with 10 recipes ordered, and
// the key a review computes to skip an unchanged run.
func BenchmarkAdviseStationsLarge(b *testing.B) {
	f := stationFactsFixture()
	f.Survey = loadSurvey(b, baselineSurveyPath)
	plan, ok := derivePlan(f.Survey, 30, nil)
	if !ok {
		b.Fatal("no plan")
	}
	f.Plan, f.Built = plan.WithRoomIDs(), allPins(plan.WithRoomIDs())
	f.Colonists = domain.Known(int64(30))
	var orders []OrderSpec
	recipes := map[string]RecipeFlow{}
	bench := f.Catalog.Stations["Bench"]
	bench.Capacity.Recipes = nil
	for i := range 10 {
		name := fmt.Sprintf("MakeThing%d", i)
		recipes[name] = RecipeFlow{Ingredients: [][]Amount{{{Resource: "Steel", Count: 2}}}, Products: []Amount{{Resource: "Thing", Count: 1}}}
		orders = append(orders, OrderSpec{Recipe: name, BenchKind: "Bench", Mode: domain.StockTarget, Target: 3})
		bench.Capacity.Recipes = append(bench.Capacity.Recipes, RecipeWorkAmount{name, 30000})
	}
	f.Catalog.Stations["Bench"], f.Recipes, f.Orders = bench, recipes, domain.Known(orders)
	b.Run("chain", func(b *testing.B) {
		for b.Loop() {
			if _, err := AdviseStations(f); err != nil {
				b.Fatal(err)
			}
		}
	})
	b.Run("key", func(b *testing.B) {
		for b.Loop() {
			_ = f.InputsKey()
		}
	})
}
