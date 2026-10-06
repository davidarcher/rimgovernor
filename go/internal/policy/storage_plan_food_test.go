package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// unroofedGround is a 40x40 open map: walkable, unroofed, no walls or roof yet.
func unroofedGround() []SiteCell {
	var cells []SiteCell
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Roofed: domain.Known(false), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	return cells
}

// storeCreates are the creates the declared stores make on open ground, by
// role prefix.
func storeCreates(view StorageRequest) map[string]StockpileEdit {
	view.Bounds, view.Cells = Bounds{Width: 40, Height: 40}, unroofedGround()
	review := PlanStockpileMaintenance(StockpileRequest{Tick: 1, Bounds: view.Bounds, Cells: view.Cells, Protected: view.Protected, Colonists: domain.Known(int64(3)), Stores: DeclareStores(view).Stores})
	out := map[string]StockpileEdit{}
	for _, e := range review.Edits {
		if e.Kind == StockpileCreate {
			out[e.Role] = e
		}
	}
	return out
}

func withinRect(cells []domain.Cell, r Rectangle) bool {
	for _, c := range cells {
		if c.X < r.X || c.X >= r.X+r.Width || c.Z < r.Z || c.Z >= r.Z+r.Height {
			return false
		}
	}
	return len(cells) > 0
}

// The food store is declared from the planned kitchen at plan time, before
// walls and roof: a Preferred 3x3 inside the kitchen at its door, with no
// kitchen room read from the census.
func TestFoodStoreIsSitedInThePlannedKitchenAtPlanTime(t *testing.T) {
	t.Parallel()
	kitchen := PlannedRoom{Role: PlannedKitchen, Interior: Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 15}, DoorRot: domain.South}
	view := StorageRequest{Layout: &LayoutPlan{Rooms: []PlannedRoom{kitchen}}, Food: &FoodStore{}}
	food, ok := storeCreates(view)[domain.FoodRole]
	if !ok || len(food.Cells) != 9 || food.Filter != domain.FoodFilter() || food.Priority != domain.PreferredPriority || !withinRect(food.Cells, kitchen.Interior) {
		t.Fatalf("food %+v", food)
	}
	var nearDoor bool
	for _, c := range food.Cells {
		nearDoor = nearDoor || c.Z == 14
	}
	if !nearDoor {
		t.Fatalf("food not at the kitchen door: %v", food.Cells)
	}
	// No kitchen planned, or storage already met: nothing.
	if got := storeCreates(StorageRequest{Layout: &LayoutPlan{}, Food: &FoodStore{}}); len(got) != 0 {
		t.Fatalf("%+v", got)
	}
	if got := storeCreates(StorageRequest{Layout: &LayoutPlan{Rooms: []PlannedRoom{kitchen}}}); len(got) != 0 {
		t.Fatalf("storage met: %+v", got)
	}
}

// A standing food zone in the kitchen is the store's zone: nothing is created
// or moved.
func TestFoodStoreStandingInTheKitchenIsLeftAlone(t *testing.T) {
	t.Parallel()
	kitchen := PlannedRoom{Role: PlannedKitchen, Interior: Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 15}}
	view := StorageRequest{Bounds: Bounds{Width: 40, Height: 40}, Cells: unroofedGround(), Layout: &LayoutPlan{Rooms: []PlannedRoom{kitchen}}, Food: &FoodStore{}}
	zone := StockpileZone{ID: "food", Role: domain.FoodRole, Cells: rectCells(Rectangle{X: 10, Z: 12, Width: 3, Height: 3}), Filter: domain.FoodFilter(), Priority: domain.PreferredPriority}
	review := PlanStockpileMaintenance(StockpileRequest{Tick: 1, Bounds: view.Bounds, Cells: view.Cells, Colonists: domain.Known(int64(3)), Zones: []StockpileZone{zone}, Stores: DeclareStores(view).Stores})
	if len(review.Edits) != 0 {
		t.Fatalf("%+v", review.Edits)
	}
}
