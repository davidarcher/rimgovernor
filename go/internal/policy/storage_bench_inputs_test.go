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

// The stonecutter gets a chunk stockpile and a recipe bench its inputs
// zone, each a keyed Important allow-list site on a roofed patch of the
// bench's room, above the general store's Low priority.
func TestPlanStorageWorkstationStockpiles(t *testing.T) {
	t.Parallel()
	room, cells := workshopRoom(8, 4)
	rooms := RoomObservation{Rooms: []Room{room}}
	plan := PlanStorage(StorageRequest{Bounds: Bounds{Width: 10, Height: 10}, Cells: cells, Rooms: &rooms, BenchInputs: []BenchInput{
		{Bench: "Bench_1", Cell: domain.Cell{X: 0, Z: 0}, Inputs: []string{"ChunkGranite"}},
		{Bench: "Bench_2", Cell: domain.Cell{X: 7, Z: 3}, Inputs: []string{"Steel"}},
		{Bench: "Bench_3", Cell: domain.Cell{X: 40, Z: 40}, Inputs: []string{"Steel"}},
	}})
	if len(plan.Sites) != 2 {
		t.Fatalf("planned %+v", plan.Sites)
	}
	for i, want := range []struct{ role, def string }{{"ingredients:Bench_1", "ChunkGranite"}, {"ingredients:Bench_2", "Steel"}} {
		site := plan.Sites[i]
		allow, ok := site.Filter.AllowOnlyDefinitions()
		if site.Role != want.role || !site.Keyed || site.Priority != domain.ImportantPriority || !ok || !slices.Equal(allow, []string{want.def}) || len(site.Candidates) == 0 || len(site.Candidates[0]) != 4 {
			t.Fatalf("site %d: %+v", i, site)
		}
	}
	if near := plan.Sites[0].Candidates[0]; !slices.Contains(near, domain.Cell{X: 0, Z: 1}) && !slices.Contains(near, domain.Cell{X: 1, Z: 0}) && !slices.Contains(near, domain.Cell{X: 1, Z: 1}) {
		t.Fatalf("first patch is not beside its bench: %v", near)
	}
	if got := PlanStorage(StorageRequest{BenchInputs: []BenchInput{{Bench: "Bench_1", Inputs: []string{"Steel"}}}}).Sites; len(got) != 0 {
		t.Fatalf("planned without a room census: %+v", got)
	}
}

// Keyed sites sharing a prefix are served, moved and created per bench.
func TestKeyedSitesAreMatchedByWholeRole(t *testing.T) {
	t.Parallel()
	room, cells := workshopRoom(8, 4)
	other := []domain.Cell{{X: 20, Z: 20}}
	first := StockpileSite{Role: "ingredients:Bench_1", Keyed: true, Room: room.Cells, Priority: domain.ImportantPriority, Candidates: [][]domain.Cell{{{X: 0, Z: 0}}}}
	second := StockpileSite{Role: "ingredients:Bench_2", Keyed: true, Room: room.Cells, Priority: domain.ImportantPriority, Candidates: [][]domain.Cell{{{X: 5, Z: 0}}}}
	r := StockpileRequest{Bounds: Bounds{Width: 30, Height: 30}, Cells: cells, Sited: []StockpileSite{first, second},
		Zones: []StockpileZone{{ID: "Zone_1", Role: "ingredients:Bench_1", Cells: []domain.Cell{{X: 0, Z: 0}}}}}
	edits := stockpileSiteEdits(r, newStockpileOpen(r))
	if len(edits) != 1 || edits[0].Role != "ingredients:Bench_2" || edits[0].Kind != StockpileCreate {
		t.Fatalf("one bench's zone must not serve the other: %+v", edits)
	}
	r.Zones = append(r.Zones, StockpileZone{ID: "Zone_2", Role: "ingredients:Bench_2", Cells: other})
	moves := stockpileSiteMoves(r)
	if len(moves) != 1 || moves[0].Zone != "Zone_2" || moves[0].Kind != StockpileDelete {
		t.Fatalf("only the zone outside its own bench's room moves: %+v", moves)
	}
}
