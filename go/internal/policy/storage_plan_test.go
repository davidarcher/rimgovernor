package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The Food department declares nothing without a layout or a meal store.
func TestFoodStoresUnknownLayoutDeclareNothing(t *testing.T) {
	t.Parallel()
	if got := DeclareStores(StorageRequest{}).Stores; len(got) != 0 {
		t.Fatalf("empty view declared %+v", got)
	}
}

// The meal closet is declared whole from the planned closet at plan time,
// before walls and roof, at Critical priority.
func TestMealClosetIsSitedWholeFromThePlan(t *testing.T) {
	t.Parallel()
	closet := PlannedRoom{Role: PlannedMealCloset, Interior: Rectangle{X: 14, Z: 21, Width: 2, Height: 2}, Door: domain.Cell{X: 15, Z: 20}}
	got := storeCreates(StorageRequest{Layout: &LayoutPlan{Rooms: []PlannedRoom{closet}}})
	edit, ok := got[plannedKey(domain.MealsRolePrefix, closet.Interior)]
	if len(got) != 1 || !ok || len(edit.Cells) != 4 || edit.Priority != domain.CriticalPriority || edit.Filter != domain.MealShelfFilter() || !withinRect(edit.Cells, closet.Interior) {
		t.Fatalf("%+v", got)
	}
}

// The table cell is one cell of the cooked meal in the planned dining room,
// off the chairs, nearest the table; retired, it is deleted without touching
// the closet's zone.
func TestMealTableCellIsOneCellOffTheChairs(t *testing.T) {
	t.Parallel()
	dining := Rectangle{X: 10, Z: 10, Width: 10, Height: 10}
	adjacent := []domain.Cell{{X: 14, Z: 15}, {X: 16, Z: 15}, {X: 14, Z: 16}, {X: 16, Z: 16}}
	filter, err := domain.AllowOnlyFilter([]string{"MealSimple"})
	if err != nil {
		t.Fatal(err)
	}
	meals := MealStore{Dining: dining, Filter: filter, Anchor: domain.Cell{X: 15, Z: 15}, Avoid: adjacent}
	got := storeCreates(StorageRequest{Meals: &meals})
	edit, ok := got[plannedKey(domain.MealsRolePrefix, dining)]
	if !ok || len(edit.Cells) != 1 || edit.Priority != domain.CriticalPriority || edit.Filter != filter {
		t.Fatalf("%+v", got)
	}
	for _, a := range adjacent {
		if a == edit.Cells[0] {
			t.Fatal("meal cell on a chair", a)
		}
	}
	if c := edit.Cells[0]; c.X < 14 || c.X > 16 || c.Z < 14 || c.Z > 17 {
		t.Fatal("meal cell far from the table", c)
	}

	closet := PlannedRoom{Role: PlannedMealCloset, Interior: Rectangle{X: 14, Z: 21, Width: 2, Height: 2}, Door: domain.Cell{X: 15, Z: 20}}
	meals.Retired = true
	view := StorageRequest{Bounds: Bounds{Width: 40, Height: 40}, Cells: unroofedGround(), Layout: &LayoutPlan{Rooms: []PlannedRoom{closet}}, Meals: &meals}
	zones := []StockpileZone{
		{ID: "table", Role: plannedKey(domain.MealsRolePrefix, dining), Cells: []domain.Cell{{X: 15, Z: 17}}, Filter: filter, Priority: domain.CriticalPriority},
		{ID: "closet", Role: plannedKey(domain.MealsRolePrefix, closet.Interior), Cells: rectCells(closet.Interior), Filter: domain.MealShelfFilter(), Priority: domain.CriticalPriority},
	}
	review := PlanStockpileMaintenance(StockpileRequest{Tick: 1, Bounds: view.Bounds, Cells: view.Cells, Colonists: domain.Known(int64(3)), Zones: zones, Stores: DeclareStores(view).Stores})
	if len(review.Edits) != 1 || review.Edits[0].Kind != StockpileDelete || review.Edits[0].Zone != "table" {
		t.Fatalf("%+v", review.Edits)
	}
}
