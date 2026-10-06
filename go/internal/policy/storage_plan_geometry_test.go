package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// freezerStoreCreates are the creates the freezer's declared stores make on a
// 30x30 open map.
func freezerStoreCreates(layout LayoutPlan) map[string]StockpileEdit {
	var cells []SiteCell
	for x := int32(0); x < 30; x++ {
		for z := int32(0); z < 30; z++ {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Roofed: domain.Known(true), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	view := StorageRequest{Bounds: Bounds{Width: 30, Height: 30}, Cells: cells, Layout: &layout}
	review := PlanStockpileMaintenance(StockpileRequest{Tick: 1, Bounds: view.Bounds, Cells: cells, Stores: DeclareStores(view).Stores})
	out := map[string]StockpileEdit{}
	for _, e := range review.Edits {
		out[stockpileRolePrefix(e.Role)] = e
	}
	return out
}

func TestFreezerStoresSitAtTheKitchenDoorAndTheRestIsPerishables(t *testing.T) {
	t.Parallel()
	// A 5x5 freezer interior at (10..14, 10..14); its outer door is on the
	// far (east) wall, its Link into the kitchen on the west wall at (9,12).
	link := domain.Cell{X: 9, Z: 12}
	freezer := PlannedRoom{Role: PlannedFreezer, Interior: Rectangle{X: 10, Z: 10, Width: 5, Height: 5}, Door: domain.Cell{X: 15, Z: 12}, Link: &link}
	layout := LayoutPlan{Rooms: []PlannedRoom{{Role: PlannedKitchen, Interior: Rectangle{X: 3, Z: 10, Width: 5, Height: 5}}, freezer}}
	creates := freezerStoreCreates(layout)
	meat, veg, corpses, rest := creates["rawmeat"], creates["rawveg"], creates["corpses"], creates["perishables"]
	for _, shelf := range []StockpileEdit{meat, veg, corpses} {
		if len(shelf.Cells) != 4 || shelf.Priority != domain.CriticalPriority {
			t.Fatalf("shelf %+v, want a Critical 2x2", shelf)
		}
		for _, c := range shelf.Cells {
			if c.X < 10 || c.X > 13 || c.Z < 10 || c.Z > 14 {
				t.Fatal("shelf must sit inside the freezer against the kitchen door wall", shelf.Cells)
			}
		}
	}
	if meat.Cells[0].X > 11 || meat.Cells[0].Z < 11 || meat.Cells[0].Z > 12 {
		t.Fatal("the first shelf takes the patch at the door", meat.Cells)
	}
	if rest.Priority != domain.PreferredPriority || len(rest.Cells) == 0 || sharesCell(rest.Cells, meat.Cells, veg.Cells, corpses.Cells) {
		t.Fatalf("perishables %+v, want ground the shelves leave", rest)
	}
	// Without a Link (plans before #819) the outer door anchors the stock.
	freezer.Link = nil
	layout.Rooms[1] = freezer
	if outer := freezerStoreCreates(layout)["rawmeat"]; len(outer.Cells) != 4 || outer.Cells[0].X < 13 {
		t.Fatal(outer)
	}
	// No planned freezer means no stock.
	if none := freezerStoreCreates(LayoutPlan{}); len(none) != 0 {
		t.Fatal(none)
	}
}

// A freezer sharing a door with dining carries a Critical meal shelf at that
// door from plan time, so the perishables catch-all never takes its ground.
func TestFreezerMealShelfAtTheDiningDoorIsPlannedWithTheFreezer(t *testing.T) {
	t.Parallel()
	link := domain.Cell{X: 15, Z: 12}
	freezer := PlannedRoom{Role: PlannedFreezer, Interior: Rectangle{X: 10, Z: 10, Width: 5, Height: 5}, Door: domain.Cell{X: 12, Z: 9}}
	dining := PlannedRoom{Role: PlannedDining, Interior: Rectangle{X: 16, Z: 10, Width: 9, Height: 7}, Door: domain.Cell{X: 20, Z: 9}, Link: &link}
	creates := freezerStoreCreates(LayoutPlan{Rooms: []PlannedRoom{freezer, dining}})
	meals := creates["meals"]
	if len(meals.Cells) != 4 || meals.Priority != domain.CriticalPriority || meals.Filter != domain.MealShelfFilter() {
		t.Fatalf("meals %+v", meals)
	}
	for _, c := range meals.Cells {
		if c.X < 13 {
			t.Fatal("meal shelf must sit at the dining door", meals.Cells)
		}
	}
	if rest := creates["perishables"]; len(rest.Cells) == 0 || sharesCell(rest.Cells, meals.Cells, creates["rawmeat"].Cells, creates["rawveg"].Cells, creates["corpses"].Cells) {
		t.Fatalf("perishables %+v, want ground the four shelves leave", rest)
	}
}

func sharesCell(cells []domain.Cell, others ...[]domain.Cell) bool {
	taken := map[domain.Cell]bool{}
	for _, o := range others {
		for _, c := range o {
			taken[c] = true
		}
	}
	for _, c := range cells {
		if taken[c] {
			return true
		}
	}
	return false
}
