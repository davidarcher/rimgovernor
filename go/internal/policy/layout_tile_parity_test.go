package policy

import (
	"errors"
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
	return colonyStationFacts(f.pawns, s)
}

// colonyStationFacts is replayStationFacts for a colony of pawns.
func colonyStationFacts(pawns int, s MapSurvey) StationFacts {
	facts := stationFactsFixture()
	stove := facts.Catalog.Stations["Stove"]
	stove.Capacity = StationCapacity{Kind: CapacityWorkers, Slots: 1, Recipes: []RecipeWorkAmount{{"CookMeal", 450}}}
	facts.Catalog.Stations["Stove"] = stove
	facts.Recipes["CookMeal"] = RecipeFlow{Food: true, Ingredients: [][]Amount{{{Resource: "Rice", Count: 1}}}, Products: []Amount{{Resource: "Meal", Count: 1}}}
	facts.Items = ItemFacts{Nutrition: map[Resource]float64{"Meal": 0.9}}
	withPowerSupply(&facts)
	facts.StackLimit["Meal"], facts.StackLimit["Rice"] = 10, 75
	facts.Perishable["Meal"] = true
	consumers := make([]FoodConsumer, pawns)
	for i := range consumers {
		consumers[i] = FoodConsumer{ID: PawnID(strconv.Itoa(i + 1)), NutritionPerDay: domain.Known(1.6)}
	}
	facts.Colonists = domain.Known(int64(pawns))
	facts.Food = domain.Known(FoodSupply{Complete: domain.Known(true), Consumers: consumers})
	facts.Plan = LayoutPlan{Zones: Zone(s)}
	facts.Built = map[int32]bool{}
	facts.Survey = s
	return facts
}

// tiledParityPlan plans a survey as the runtime does: DeriveLayoutPlan, the
// station chain and the tiler with the perimeter and the sites laid over.
func tiledParityPlan(f replayFixture, s MapSurvey) (LayoutPlan, error) {
	fact, _, err := DeriveLayoutPlan(s, replayStationFacts(f, s), nil, 0)
	plan, known := fact.Value()
	if err == nil && !known {
		err = errors.New("the derive holds no core")
	}
	return plan, err
}

// tilerParityExpectedFail lists the replay fixtures the tiled plan does not
// yet bring to parity with the old generator (ParityVerdict), with the reason.
// The list is the input to the cutover (#2931).
var tilerParityExpectedFail = map[string]string{}

// TestTilerParityGate measures the gap between the station-chain tiler and
// the old generator on every replay fixture. It changes nothing at runtime.
func TestTilerParityGate(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	RunParityGate(t, tiledParityPlan, tilerParityExpectedFail)
}
