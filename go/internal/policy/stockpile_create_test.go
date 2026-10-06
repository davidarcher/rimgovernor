package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// stockpileCreateRequest is a 20x20 map: x<10 an indoor roofed room
// holding a 2x2 general store at (2,2), x>=10 open ground.
func stockpileCreateRequest() StockpileRequest {
	r := StockpileRequest{Tick: 100, Bounds: Bounds{Width: 20, Height: 20}, Colonists: domain.Known(int64(3)), Anchor: domain.Cell{X: 10, Z: 10}}
	var store []domain.Cell
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			inside := x < 10
			zone := inside && x >= 2 && x < 4 && z >= 2 && z < 4
			c := SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(zone), Roofed: domain.Known(inside), Indoors: domain.Known(inside), StorageEmpty: domain.Known(true)}
			r.Cells = append(r.Cells, c)
			if zone {
				store = append(store, c.Cell)
			}
		}
	}
	r.Zones = []StockpileZone{{ID: "Zone_1", Role: domain.GeneralRole, Cells: store, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority}}
	return r
}

// A shelf takes its zone's desired settings (the role's published state
// over the zone's own): an unpatched shelf and one whose zone's desired
// settings moved are patched, one already matching is left alone.
func TestStockpileShelvesFollowTheirZone(t *testing.T) {
	r := stockpileCreateRequest()
	food := domain.FoodFilter()
	r.Roles = func(role string) (StockpileRoleState, bool) {
		return StockpileRoleState{Filter: food, Priority: domain.ImportantPriority}, role == domain.GeneralRole
	}
	r.Zones[0].Filter, r.Zones[0].Priority = food, domain.ImportantPriority
	r.Shelves = []StockpileShelf{
		{Building: "Shelf_1", Zone: "Zone_1", Cells: 2},
		{Building: "Shelf_2", Zone: "Zone_1", Cells: 2, Patched: true, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
		{Building: "Shelf_3", Zone: "Zone_1", Cells: 2, Patched: true, Filter: food, Priority: domain.ImportantPriority},
		{Building: "Shelf_4", Zone: "Zone_9", Cells: 2},
	}
	review := PlanStockpileMaintenance(r)
	var patched []string
	for _, e := range review.Edits {
		if e.Kind != StockpileShelfPatch {
			t.Fatalf("unexpected edit %+v", e)
		}
		if e.Filter != food || e.Priority != domain.ImportantPriority || e.Role != ShelfRole(e.Zone) || e.Hauls != 2*ShelfItemsPerCell {
			t.Fatalf("shelf edit %+v", e)
		}
		patched = append(patched, e.Zone)
	}
	if len(patched) != 2 || patched[0] != "Shelf_1" || patched[1] != "Shelf_2" {
		t.Fatalf("patched %v", patched)
	}
}
