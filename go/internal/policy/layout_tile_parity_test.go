package policy

import (
	"strconv"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// replayStationFacts is the StationFacts of a replay fixture: the test
// catalog's stations, a colony of f.pawns eating nutritionPerPawn a day, no
// built rooms, and the survey's zones as the plan. It is the demand the old
// generator was scored against, read through the station chain.
func replayStationFacts(f replayFixture, s MapSurvey) StationFacts {
	facts := stationFactsFixture()
	stove := facts.Catalog.Stations["Stove"]
	stove.Capacity = StationCapacity{Kind: CapacityWorkers, Slots: 1, Recipes: []RecipeWorkAmount{{"CookMeal", 450}}}
	facts.Catalog.Stations["Stove"] = stove
	facts.Recipes["CookMeal"] = RecipeFlow{Food: true, Ingredients: [][]Amount{{{Resource: "Rice", Count: 1}}}, Products: []Amount{{Resource: "Meal", Count: 1}}}
	facts.Items = ItemFacts{Nutrition: map[Resource]float64{"Meal": 0.9}}
	facts.StackLimit["Meal"], facts.StackLimit["Rice"] = 10, 75
	facts.Perishable["Meal"] = true
	consumers := make([]FoodConsumer, f.pawns)
	for i := range consumers {
		consumers[i] = FoodConsumer{ID: PawnID(strconv.Itoa(i + 1)), NutritionPerDay: domain.Known(1.6)}
	}
	facts.Colonists = domain.Known(int64(f.pawns))
	facts.Food = domain.Known(FoodSupply{Complete: domain.Known(true), Consumers: consumers})
	facts.Plan = LayoutPlan{Zones: Zone(s)}
	facts.Built = map[int32]bool{}
	facts.Survey = s
	facts.Tile = true
	return facts
}

// tiledParityPlan plans a survey through the station chain and the tiler.
func tiledParityPlan(f replayFixture, s MapSurvey) (LayoutPlan, error) {
	advice, err := AdviseStations(replayStationFacts(f, s))
	if err != nil {
		return LayoutPlan{}, err
	}
	return advice.Tiled.Plan, nil
}

// tilerParityExpectedFail lists the replay fixtures the tiled plan does not
// yet bring to parity with the old generator (ParityVerdict), with the reason.
// The list is the input to the cutover (#2931).
var tilerParityExpectedFail = map[string]string{
	"baseline":          "total 2037 below the old generator: Centre -6757 (the tiled rooms sit farther from the map centre) outweighs the Soil, Footprint and Wall gains",
	"hundred-colonists": "total 198359 below the old generator: Edge -140289 and Centre -46285 (a 100-bedroom block is placed far from the edge and centre the old generator packed to) and Wall -11220",
}

// TestTilerParityGate measures the gap between the station-chain tiler and
// the old generator on every replay fixture. It changes nothing at runtime.
func TestTilerParityGate(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	RunParityGate(t, tiledParityPlan, tilerParityExpectedFail)
}
