package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func benchWithBill(id, recipe string, active domain.Fact[bool], ingredients ...[]Amount) GearBench {
	return GearBench{
		ID:      id,
		Bills:   domain.Known([]GearBill{{ID: "Bill_" + id, Recipe: recipe, Active: active}}),
		Recipes: domain.Known([]GearRecipe{{Definition: recipe, Ingredients: domain.Known(ingredients)}}),
	}
}

func coveredSiteCell(x, z int32, walkable, occupied, zone, roofed, storageEmpty bool) SiteCell {
	return SiteCell{
		Cell:         domain.Cell{X: x, Z: z},
		Walkable:     domain.Known(walkable),
		Occupied:     domain.Known(occupied),
		Zone:         domain.Known(zone),
		Roofed:       domain.Known(roofed),
		StorageEmpty: domain.Known(storageEmpty),
	}
}

func coveredStorageGrid(width, height int32, fn func(x, z int32) SiteCell) []SiteCell {
	var cells []SiteCell
	for x := int32(0); x < width; x++ {
		for z := int32(0); z < height; z++ {
			cells = append(cells, fn(x, z))
		}
	}
	return cells
}

func workshopRoom(width, height int32) (Room, []SiteCell) {
	room := Room{ID: "Room_1"}
	cells := coveredStorageGrid(width, height, func(x, z int32) SiteCell {
		room.Cells = append(room.Cells, domain.Cell{X: x, Z: z})
		return coveredSiteCell(x, z, true, false, false, true, true)
	})
	return room, cells
}

// A bench with an active bill is listed with the union of its recipe's
// ingredient alternatives; unobserved or idle benches, benches at no known
// cell and the food planner's benches are not.
func TestDeriveBenchInputs(t *testing.T) {
	t.Parallel()
	chunks := [][]Amount{{{Resource: "ChunkSandstone"}}, {{Resource: "ChunkGranite"}}}
	idle := benchWithBill("Bench_2", "Smelt", domain.Known(false), []Amount{{Resource: "Steel"}})
	unknownBills := benchWithBill("Bench_3", "Smelt", domain.Known(true), []Amount{{Resource: "Steel"}})
	unknownBills.Bills = domain.Unknown[[]GearBill]()
	unknownIngredients := benchWithBill("Bench_4", "Smelt", domain.Known(true))
	unknownIngredients.Recipes = domain.Known([]GearRecipe{{Definition: "Smelt", Ingredients: domain.Unknown[[][]Amount]()}})
	benches := []GearBench{
		benchWithBill("Bench_7", "MakeStoneBlocks", domain.Known(true), chunks...),
		idle, unknownBills, unknownIngredients,
		benchWithBill("Bench_5", "Cook", domain.Known(true), []Amount{{Resource: "Meat"}}),
		benchWithBill("Bench_6", "Weave", domain.Known(true), []Amount{{Resource: "Cloth"}}),
	}
	at := map[string]domain.Cell{"Bench_2": {X: 1, Z: 1}, "Bench_3": {X: 1, Z: 1}, "Bench_4": {X: 1, Z: 1}, "Bench_5": {X: 1, Z: 1}, "Bench_7": {X: 2, Z: 2}}
	got := DeriveBenchInputs(benches, at, map[string]bool{"Bench_5": true})
	if len(got) != 1 || got[0].Bench != "Bench_7" || got[0].Cell != (domain.Cell{X: 2, Z: 2}) || !slices.Equal(got[0].Inputs, []string{"ChunkGranite", "ChunkSandstone"}) {
		t.Fatalf("%+v", got)
	}
}

func benchStores(view StoreView) []Store { return industryOwner{}.Stores(view) }

// The stonecutter gets a chunk store and a recipe bench its inputs store, each a
// keyed Important allow-list 2x2 on roofed ground of the bench's room, above
// the general store's Low priority, the first beside its bench.
func TestIndustryDeclaresBenchStores(t *testing.T) {
	t.Parallel()
	room, cells := workshopRoom(8, 4)
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{room}}
	view := StoreView{Bounds: Bounds{Width: 10, Height: 10}, Cells: cells, Rooms: &rooms, BenchInputs: []BenchInput{
		{Bench: "Bench_1", Cell: domain.Cell{X: 0, Z: 0}, Inputs: []string{"ChunkGranite"}},
		{Bench: "Bench_2", Cell: domain.Cell{X: 7, Z: 3}, Inputs: []string{"Steel"}},
		{Bench: "Bench_3", Cell: domain.Cell{X: 40, Z: 40}, Inputs: []string{"Steel"}},
	}}
	stores := benchStores(view)
	if len(stores) != 2 {
		t.Fatalf("declared %+v", stores)
	}
	open := newStockpileOpen(StockpileRequest{Cells: cells, Bounds: view.Bounds})
	for i, want := range []struct{ role, def string }{{"ingredients:Bench_1", "ChunkGranite"}, {"ingredients:Bench_2", "Steel"}} {
		store := stores[i]
		allow, ok := store.Filter.AllowOnlyDefinitions()
		if store.Role != want.role || !store.exact || store.Priority != domain.ImportantPriority || store.Retired || !ok || !slices.Equal(allow, []string{want.def}) || len(store.Cells(open)) != 4 {
			t.Fatalf("store %d: %+v", i, store)
		}
	}
	if near := stores[0].Cells(open); !slices.Contains(near, domain.Cell{X: 0, Z: 1}) && !slices.Contains(near, domain.Cell{X: 1, Z: 0}) && !slices.Contains(near, domain.Cell{X: 1, Z: 1}) {
		t.Fatalf("first patch is not beside its bench: %v", near)
	}
	if got := benchStores(StoreView{BenchInputs: []BenchInput{{Bench: "Bench_1", Inputs: []string{"Steel"}}}}); len(got) != 0 {
		t.Fatalf("declared without a room census: %+v", got)
	}
	if demand := (industryOwner{}).RoomDemand(view); demand != (RoomDemand{}) {
		t.Fatalf("bench stores ask layout for rooms: %+v", demand)
	}
}

// A store is created per bench, never served by another bench's zone, and
// retires only when its bench is gone from the census; an unread census retires
// nothing, and a bench with its bill paused keeps its zone.
func TestBenchStoresAreKeyedAndRetireWithTheirBench(t *testing.T) {
	t.Parallel()
	room, cells := workshopRoom(8, 4)
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{room}}
	steel, err := domain.AllowOnlyFilter([]string{"Steel"})
	if err != nil {
		t.Fatal(err)
	}
	zone := StockpileZone{ID: "Zone_1", Role: "ingredients:Bench_1", Cells: []domain.Cell{{X: 0, Z: 0}, {X: 0, Z: 1}, {X: 1, Z: 0}, {X: 1, Z: 1}}, Filter: steel, Priority: domain.ImportantPriority}
	view := StoreView{Bounds: Bounds{Width: 30, Height: 30}, Cells: cells, Rooms: &rooms, Zones: []StockpileZone{zone},
		Benches: domain.Known(map[string]bool{"Bench_1": true, "Bench_2": true}),
		BenchInputs: []BenchInput{
			{Bench: "Bench_1", Cell: domain.Cell{X: 0, Z: 0}, Inputs: []string{"Steel"}},
			{Bench: "Bench_2", Cell: domain.Cell{X: 7, Z: 3}, Inputs: []string{"Steel"}},
		}}
	open := newStockpileOpen(StockpileRequest{Cells: cells, Bounds: view.Bounds})
	edits, _ := declaredStoreEdits(view.Zones, benchStores(view), open)
	creates := 0
	for _, e := range edits {
		if e.Kind != StockpileCreate || e.Role != "ingredients:Bench_2" {
			t.Fatalf("one bench's zone must not serve the other: %+v", edits)
		}
		creates++
	}
	if creates != 1 {
		t.Fatalf("edits %+v", edits)
	}
	// Bill paused: the bench stands, no input, the zone stays.
	view.BenchInputs = nil
	if edits, _ := declaredStoreEdits(view.Zones, benchStores(view), open); len(edits) != 0 {
		t.Fatalf("a paused bench lost its store: %+v", edits)
	}
	// Bench demolished: the zone is deleted.
	view.Benches = domain.Known(map[string]bool{"Bench_2": true})
	edits, _ = declaredStoreEdits(view.Zones, benchStores(view), open)
	if len(edits) != 1 || edits[0].Kind != StockpileDelete || edits[0].Zone != "Zone_1" {
		t.Fatalf("demolished bench: %+v", edits)
	}
	view.Benches = domain.Unknown[map[string]bool]()
	if edits, _ := declaredStoreEdits(view.Zones, benchStores(view), open); len(edits) != 0 {
		t.Fatalf("retired over an unread census: %+v", edits)
	}
}
