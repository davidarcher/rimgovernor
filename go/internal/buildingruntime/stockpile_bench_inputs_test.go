package buildingruntime

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func censusBench(t *testing.T, id, recipe string, ingredients ...policy.Amount) bridge.GearBenchRead {
	t.Helper()
	return bridge.GearBenchRead{Bench: policy.GearBench{
		ID:      id,
		Bills:   domain.Known([]policy.GearBill{{ID: "Bill_" + id, Recipe: recipe, Active: domain.Known(true)}}),
		Recipes: domain.Known([]policy.GearRecipe{{Definition: recipe, Ingredients: domain.Known([][]policy.Amount{ingredients})}}),
	}}
}

// The runtime joins the bench census to the built-building census for each
// bench's cell, and leaves the kitchen's benches to the food stores.
func TestBenchInputsJoinsCensusAndLeavesFoodBenches(t *testing.T) {
	t.Parallel()
	building := func(def string, x int32) policy.CurrentBuilding {
		b, err := domain.NewBuilding(def, domain.Cell{X: x, Z: 4}, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		return policy.CurrentBuilding{ID: "Bench_" + def, Building: b}
	}
	projection := &observation.ColonyProjection{}
	projection.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{building("A", 3), building("B", 6)}})
	projection.ProductionBenches = domain.Known([]policy.ProductionBench{{ID: "Bench_B"}})
	census := []bridge.GearBenchRead{
		censusBench(t, "Bench_A", "MakeStoneBlocks", policy.Amount{Resource: "ChunkGranite"}),
		censusBench(t, "Bench_B", "Cook", policy.Amount{Resource: "Meat"}),
	}
	got := benchInputs(census, projection)
	if len(got) != 1 || got[0].Bench != "Bench_A" || got[0].Cell != (domain.Cell{X: 3, Z: 4}) || !slices.Equal(got[0].Inputs, []string{"ChunkGranite"}) {
		t.Fatalf("%+v", got)
	}
	projection.ProductionBenches = domain.Unknown[[]policy.ProductionBench]()
	if got = benchInputs(census, projection); len(got) != 0 {
		t.Fatalf("planned with the food benches unobserved: %+v", got)
	}
}
