package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func riceCrop() CropChoice {
	return CropChoice{
		Name: "Plant_Rice", Edible: domain.Known(true), Harvests: domain.Known(Resource("RawRice")),
		UnitsPerCell: domain.Known(4.0), GrowDays: domain.Known(6.0), HarvestNutrition: domain.Known(0.05),
		Demand: domain.Known(1.6), FertilityMin: domain.Known(0.8),
	}
}

func fieldGraphInput() StationGraphInput {
	in := graphTestInput()
	in.Recipes = map[string]RecipeFlow{"MakeThing": {Ingredients: [][]Amount{{{Resource: "RawRice", Count: 2}}}, Products: []Amount{{Resource: "Thing", Count: 1}}}}
	in.Orders = []OrderSpec{{Recipe: "MakeThing", BenchKind: "Bench", Mode: domain.StockTarget, Target: 3}}
	in.Stock = map[Resource]int64{"RawRice": 90}
	in.StackLimit = map[Resource]int64{"RawRice": 75}
	in.Population = StationPopulation{Colonists: 3}
	in.Crops = []CropChoice{riceCrop()}
	in.FoodReserveDays = DefaultFoodReserveDays
	return in
}

// A field node asks the crop's capacity target, sits on the field mask and
// hauls to the store that holds its harvest and the station that works it.
func TestGraphCarriesFieldNodePerCrop(t *testing.T) {
	in := fieldGraphInput()
	g, err := BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	want, _ := FieldCapacityTarget(domain.Known(int64(3)), riceCrop(), DefaultFoodReserveDays).Value()
	f, ok := g.FieldNode("field/Plant_Rice")
	if !ok || len(g.Fields) != 1 {
		t.Fatalf("fields %+v", g.Fields)
	}
	if f.Area != want || f.Area <= 0 || f.Mask != ZoneField || f.FertilityMin != 1.0 || f.Harvests != "RawRice" || f.Reason != ReasonFieldDemand {
		t.Errorf("field %+v, want area %d", f, want)
	}
	if got := g.Weight(f.ID, "Shelf/store"); got != float64(want)*4/6 {
		t.Errorf("harvest to store %v", got)
	}
	if got := g.Weight(f.ID, "Bench/work"); got != 6 {
		t.Errorf("three runs take 6 rice from the field: %v", got)
	}
}

// Pasture, wood and mining are masks, not nodes; a crop that is not eaten, or
// whose target is unknown, gets none, and an unknown yield adds no flow.
func TestGraphFieldNodesSkipWhatIsNotDemand(t *testing.T) {
	in := fieldGraphInput()
	hay := riceCrop()
	hay.Name, hay.Edible = "Plant_Haygrass", domain.Known(false)
	unknown := riceCrop()
	unknown.Name, unknown.Demand = "Plant_Corn", domain.Unknown[float64]()
	blind := riceCrop()
	blind.Name, blind.UnitsPerCell = "Plant_Potato", domain.Unknown[float64]()
	in.Crops = []CropChoice{hay, unknown, blind}
	g, err := BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(g.Fields) != 1 || g.Fields[0].Crop != "Plant_Potato" {
		t.Fatalf("fields %+v", g.Fields)
	}
	if g.Weight("field/Plant_Potato", "Shelf/store") != 0 || g.Weight("field/Plant_Potato", "Bench/work") != 0 {
		t.Errorf("an unknown yield is not a flow: %+v", g.Edges)
	}
	for _, r := range g.Rooms {
		if r.Role != PlannedCirculation && r.Reason != ReasonOutskirtsRoom {
			t.Errorf("room node %+v", r)
		}
	}
}

// Fields change nothing the old graph held, and the graph is deterministic.
func TestGraphFieldNodesAreAdditiveAndDeterministic(t *testing.T) {
	in := fieldGraphInput()
	a, err := BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	for range 5 {
		b, err := BuildStationGraph(in)
		if err != nil || !reflect.DeepEqual(a, b) {
			t.Fatalf("graph differs: %v", err)
		}
	}
	in.Crops = nil
	bare, err := BuildStationGraph(in)
	if err != nil || len(bare.Fields) != 0 || !reflect.DeepEqual(bare.Nodes, a.Nodes) || !reflect.DeepEqual(bare.Rooms, a.Rooms) {
		t.Fatalf("crops changed the station nodes: %v", err)
	}
	if _, err := ClusterStations(ClusterInput{Graph: a, Catalog: clusterCatalog(), RoofDistance: testRoof}); err != nil {
		t.Errorf("clustering a graph with fields: %v", err)
	}
}
