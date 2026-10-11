package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The warehouse (the general store a MaintainStockpiles zone holds) gets its
// shelf upgrade once a Shelf is buildable; it makes the review active, ranks
// after every zone edit, and no other role or spent zone is shelved.
func TestWarehouseShelfUpgrade(t *testing.T) {
	t.Parallel()
	zone, cells := shelfRoom(5, 5, domain.Cell{X: 3, Z: 0})
	request := StockpileRequest{Cells: cells, Zones: []StockpileZone{{ID: "Zone_1", Role: domain.GeneralRole, Cells: zone, Filter: domain.GeneralFilter(), Priority: domain.LowPriority}}}
	if got := PlanStockpileMaintenance(request); got.Shelf.Kind != ShelfNone || got.Active {
		t.Fatal("an unresearched Shelf must not be placed", got)
	}
	request.ShelfAvailable = true
	got := PlanStockpileMaintenance(request)
	if got.Shelf.Kind != ShelfBuild || got.Shelf.Zone.Zone != "Zone_1" || !got.Active {
		t.Fatal(got)
	}
	request.ShelfSpent = map[string]bool{"Zone_1": true}
	if got := PlanStockpileMaintenance(request); got.Shelf.Kind != ShelfNone {
		t.Fatal("a spent zone gets no shelf", got)
	}
	request.ShelfSpent = nil
	request.Zones[0].Role = "meals:kitchen"
	if got := PlanStockpileMaintenance(request); got.Shelf.Kind != ShelfNone {
		t.Fatal("only the warehouse is shelved", got)
	}
}
