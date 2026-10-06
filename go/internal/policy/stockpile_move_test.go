package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Reserved ground stays out of the food site (#1795): protecting the cells at
// the kitchen door moves the 3x3 deeper into the kitchen.
func TestFoodSiteAvoidsProtectedCells(t *testing.T) {
	t.Parallel()
	kitchen := PlannedRoom{Role: PlannedKitchen, Interior: Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 15}}
	protected := rectCells(Rectangle{X: 10, Z: 13, Width: 6, Height: 2})
	view := StorageRequest{Layout: &LayoutPlan{Rooms: []PlannedRoom{kitchen}}, Food: &FoodStore{}, Protected: protected}
	food := storeCreates(view)[domain.FoodRole]
	if len(food.Cells) != 9 {
		t.Fatalf("food %+v", food)
	}
	for _, c := range food.Cells {
		if c.Z >= 13 {
			t.Fatalf("food sited on reserved ground: %v", c)
		}
	}
}

// A moved zone is deleted only once its replacement's create is admitted
// (#1795): with no free candidate the old zone stays, with one the review
// creates first and then deletes.
func TestMovedZoneStaysUntilItsReplacementIsCreated(t *testing.T) {
	t.Parallel()
	r := stockpileCreateRequest()
	room := rectCells(Rectangle{X: 12, Z: 12, Width: 3, Height: 3})
	site := StockpileSite{Role: domain.FoodRole, Room: room, Filter: domain.FoodFilter(), Priority: domain.PreferredPriority}
	r.Zones = []StockpileZone{{ID: "Zone_food", Role: domain.FoodRole, Cells: []domain.Cell{{X: 2, Z: 2}}, Filter: domain.FoodFilter(), Priority: domain.PreferredPriority}}
	r.Sited = []StockpileSite{site}
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind == StockpileDelete {
			t.Fatalf("zone deleted with no replacement: %+v", e)
		}
	}
	r.Sited[0].Candidates = [][]domain.Cell{room}
	var kinds []StockpileEditKind
	for _, e := range PlanStockpileMaintenance(r).Edits {
		kinds = append(kinds, e.Kind)
	}
	if len(kinds) != 2 || kinds[0] != StockpileCreate || kinds[1] != StockpileDelete {
		t.Fatalf("edits %v, want create then delete", kinds)
	}
}
