package bridge

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// baselineGraphInput is a five-colonist start on the recorded catalog: the
// colonists eat 2.6 nutrition a day each, the stock demand holds the
// ingredients a kitchen works and some material.
func baselineGraphInput(t *testing.T) policy.StationGraphInput {
	t.Helper()
	catalog := sharedRecordedCatalog(t)
	shapes, err := catalog.PieceShapes()
	if err != nil {
		t.Fatal(err)
	}
	furniture, err := catalog.RoomFurniture(shapes.Defs)
	if err != nil {
		t.Fatal(err)
	}
	dining, err := catalog.DiningFurniture()
	if err != nil {
		t.Fatal(err)
	}
	items, err := catalog.ItemFacts()
	if err != nil {
		t.Fatal(err)
	}
	stations := recordedStations(t)
	recipes, err := catalog.StationRecipeFlows(stations)
	if err != nil {
		t.Fatal(err)
	}
	stock := map[policy.Resource]int64{"RawPotatoes": 300, "Meat_Muffalo": 120, "WoodLog": 400, "Steel": 200}
	limits := map[policy.Resource]int64{}
	perishable := map[policy.Resource]bool{}
	for r := range stock {
		if limits[r], err = catalog.StackLimit(string(r)); err != nil {
			t.Fatal(err)
		}
		if _, rots, err := catalog.RotDays(string(r)); err != nil {
			t.Fatal(err)
		} else {
			perishable[r] = rots
		}
	}
	// Perishability is known for every item a recipe moves, not only the
	// stocked ones.
	for _, flow := range recipes {
		var moved []policy.Amount
		for _, slot := range flow.Ingredients {
			moved = append(moved, slot...)
		}
		for _, a := range append(moved, flow.Products...) {
			if _, seen := perishable[a.Resource]; seen {
				continue
			}
			if _, rots, err := catalog.RotDays(string(a.Resource)); err != nil {
				t.Fatal(err)
			} else {
				perishable[a.Resource] = rots
			}
		}
	}
	return policy.StationGraphInput{
		Catalog: stations, Furniture: furniture, Dining: dining, Items: items, Recipes: recipes,
		Population:      policy.StationPopulation{Colonists: 5, Injured: 1, Prisoners: 1},
		NutritionPerDay: 5 * 2.6,
		Stock:           stock, StackLimit: limits, Perishable: perishable,
	}
}

func nodeCount(g policy.StationGraph, purpose policy.StationPurpose) int {
	n := 0
	for _, node := range g.Nodes {
		if node.Purpose == purpose {
			n += node.Count
		}
	}
	return n
}

func nodeOf(t *testing.T, g policy.StationGraph, purpose policy.StationPurpose) policy.StationNode {
	t.Helper()
	for _, node := range g.Nodes {
		if node.Purpose == purpose {
			return node
		}
	}
	t.Fatalf("no %s node in %+v", purpose, g.Nodes)
	return policy.StationNode{}
}

// The baseline colony needs a bed for each colonist and the spare place, a
// medical bed for the injured, one for the prisoner, a stove, a table, and
// cold storage for the perishables, joined by the flows of the kitchen.
func TestBaselineStationGraph(t *testing.T) {
	in := baselineGraphInput(t)
	g, err := policy.BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	if got, want := nodeCount(g, policy.PurposeSleep), 5+policy.HousingSparePlaces; got != want {
		t.Errorf("regular beds %d, want %d", got, want)
	}
	if nodeCount(g, policy.PurposeSleepMed) != 1 || nodeCount(g, policy.PurposeSleepPrison) != 1 {
		t.Errorf("medical/prison beds: %+v", g.Nodes)
	}
	stove := nodeOf(t, g, policy.PurposeWork)
	if stove.Count != 1 {
		t.Errorf("stoves %d, want 1 for five colonists", stove.Count)
	}
	if kitchen, ok := in.Catalog.Station(stove.Def); !ok || len(kitchen.Contributions) == 0 || kitchen.Contributions[0].Role != policy.RoomRoleKitchen {
		t.Errorf("work station %s is no kitchen station", stove.Def)
	}
	table := nodeOf(t, g, policy.PurposeDine)
	cold := nodeOf(t, g, policy.PurposeStoreCold)
	dry := nodeOf(t, g, policy.PurposeStore)
	if len(cold.Needs) == 0 || cold.Needs[0].Kind != policy.NeedCold {
		t.Errorf("cold store needs %+v", cold.Needs)
	}
	for _, need := range dry.Needs {
		if need.Kind == policy.NeedCold {
			t.Errorf("dry store wants cold")
		}
	}
	bed := nodeOf(t, g, policy.PurposeSleep)
	if !g.Requires(cold.ID, stove.ID) {
		t.Errorf("no cold storage requirement beside the stove: %+v", g.Edges)
	}
	storeToStove := g.Weight(cold.ID, stove.ID) + g.Weight(dry.ID, stove.ID)
	stoveToTable := g.Weight(stove.ID, table.ID)
	storeToBed := g.Weight(cold.ID, bed.ID) + g.Weight(dry.ID, bed.ID)
	if storeToStove <= storeToBed || stoveToTable <= storeToBed {
		t.Errorf("flows: store-stove %v, stove-table %v, store-bed %v", storeToStove, stoveToTable, storeToBed)
	}
	if g.Weight(bed.ID, table.ID) <= 0 {
		t.Errorf("no sleep-and-eat edge")
	}
}

// The graph is a pure function of its input.
func TestStationGraphDeterministic(t *testing.T) {
	in := baselineGraphInput(t)
	first, err := policy.BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	for range 5 {
		again, err := policy.BuildStationGraph(in)
		if err != nil || !reflect.DeepEqual(first, again) {
			t.Fatalf("graph changed: %v", err)
		}
	}
}

// A colony of one needs single-station nodes, and growth raises the counts
// with the models that size them.
func TestStationGraphScalesWithDemand(t *testing.T) {
	in := baselineGraphInput(t)
	in.Population = policy.StationPopulation{Colonists: 1}
	in.NutritionPerDay = 2.6
	small, err := policy.BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	if nodeCount(small, policy.PurposeSleep) != 1+policy.HousingSparePlaces || nodeCount(small, policy.PurposeDine) != 1 {
		t.Errorf("single-colonist nodes: %+v", small.Nodes)
	}
	in.Population = policy.StationPopulation{Colonists: 40}
	in.NutritionPerDay = 40 * 2.6
	large, err := policy.BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	if nodeCount(large, policy.PurposeSleep) != 40+policy.HousingSparePlaces || nodeCount(large, policy.PurposeDine) <= 1 {
		t.Errorf("forty-colonist nodes: %+v", large.Nodes)
	}
}
