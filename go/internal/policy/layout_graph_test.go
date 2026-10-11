package policy

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func graphTestInput() StationGraphInput {
	return StationGraphInput{
		Catalog: StationCatalog{Stations: map[string]Station{
			"Bed":   {Def: "Bed", Capacity: StationCapacity{Kind: CapacitySleepers, Slots: 1}},
			"Bench": {Def: "Bench", Capacity: StationCapacity{Kind: CapacityWorkers, Slots: 1, Recipes: []RecipeWorkAmount{{"MakeThing", 30000}}}},
			"Shelf": {Def: "Shelf", Shape: InteriorPieceDef{Def: "Shelf", Size: domain.Cell{X: 1, Z: 1}},
				Contributions: []RoleContribution{{Role: RoomRoleStoreroom, Weight: 1, Additive: true}},
				Capacity:      StationCapacity{Kind: CapacityItems, Slots: 4}},
		}},
		Furniture:  RoomFurniture{Beds: []FurnitureBed{{Def: "Bed", Slots: 1}}},
		Population: StationPopulation{Colonists: 1},
		Recipes:    map[string]RecipeFlow{"MakeThing": {Ingredients: [][]Amount{{{Resource: "Steel", Count: 2}}}, Products: []Amount{{Resource: "Thing", Count: 1}}}},
		Stock:      map[Resource]int64{"Steel": 90},
		StackLimit: map[Resource]int64{"Steel": 75},
	}
}

// A ledger order adds its work: the bench count follows the work owed and
// the flows join it to the store.
func TestStationGraphOrdersSizeBenches(t *testing.T) {
	in := graphTestInput()
	in.Orders = []OrderSpec{{Recipe: "MakeThing", BenchKind: "Bench", Mode: domain.StockTarget, Target: 3}}
	g, err := BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	bench, ok := g.Node("Bench/work")
	if !ok || bench.Count != 2 {
		t.Fatalf("three runs of half a day need two benches: %+v", g.Nodes)
	}
	store, _ := g.Node("Shelf/store")
	if store.Count != 1 {
		t.Errorf("two stacks fit one four-slot shelf: %+v", store)
	}
	if g.Weight("Bench/work", "Shelf/store") != 9 {
		t.Errorf("ingredient flow %v, want 6 steel in and 3 things out", g.Weight("Bench/work", "Shelf/store"))
	}
}

// A need the catalog has no station for is an error, not a guess.
func TestStationGraphUnknownsAreErrors(t *testing.T) {
	in := graphTestInput()
	in.StackLimit = nil
	if _, err := BuildStationGraph(in); !errors.Is(err, ErrStationGraph) {
		t.Errorf("missing stack limit: %v", err)
	}
	in = graphTestInput()
	in.Population.Babies = 1
	if _, err := BuildStationGraph(in); !errors.Is(err, ErrStationGraph) {
		t.Errorf("baby with no crib: %v", err)
	}
}
