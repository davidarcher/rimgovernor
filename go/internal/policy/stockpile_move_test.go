package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Reserved ground stays out of the food site (#1795): protecting the roofed
// overhang's cells sends the zone outdoors beside the kitchen.
func TestFoodSiteAvoidsProtectedCells(t *testing.T) {
	r, s := foodSiteRequests(domain.Cell{X: 20, Z: 5})
	s.Protected = rectCells(Rectangle{X: 30, Z: 30, Width: 4, Height: 4})
	r.Protected = s.Protected
	r.Sited = PlanStorage(s).Sites
	for _, c := range foodSiteCreate(t, r).Cells {
		if c.X >= 30 && c.X < 34 && c.Z >= 30 && c.Z < 34 {
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
