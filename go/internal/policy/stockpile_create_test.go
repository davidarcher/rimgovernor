package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// stockpileCreateRequest is a 20x20 map: x<10 an indoor roofed room
// holding a 2x2 general store at (2,2), x>=10 open ground.
func stockpileCreateRequest() StockpileRequest {
	r := StockpileRequest{Tick: 100, Bounds: Bounds{Width: 20, Height: 20}, Colonists: domain.Known(int64(3)), Anchor: domain.Cell{X: 10, Z: 10}, Rooms: domain.Known([]Room{})}
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

// The colony has apparel, rotten items and a raider corpse and no zone for
// any: the apparel stockpile goes indoors beside the general store, the two
// dumps outdoors on separate patches, and the weapons role (nothing to
// store) gets none.
func TestStockpileCreatesMissingRolesWithThings(t *testing.T) {
	r := stockpileCreateRequest()
	r.Needs = map[string]int{domain.ApparelRole: 3, domain.RottenDumpRole: 2, domain.CorpseDumpRole: 1}
	review := PlanStockpileMaintenance(r)
	got := map[string]StockpileEdit{}
	for _, e := range review.Edits {
		if e.Kind != StockpileCreate {
			t.Fatalf("unexpected edit %+v", e)
		}
		got[e.Role] = e
	}
	if len(got) != 3 || !review.Active {
		t.Fatalf("creates %+v", review.Edits)
	}
	apparel := got[domain.ApparelRole]
	if apparel.Filter != domain.ApparelFilter() || apparel.Priority != domain.PreferredPriority || apparel.Hauls != 3 || len(apparel.Cells) != 4 {
		t.Fatalf("apparel %+v", apparel)
	}
	for _, c := range apparel.Cells {
		if c.X >= 10 || c.X > 6 || c.Z > 6 {
			t.Fatalf("apparel not indoors near the store: %+v", apparel.Cells)
		}
	}
	taken := map[domain.Cell]string{}
	for _, role := range []string{domain.RottenDumpRole, domain.CorpseDumpRole} {
		e := got[role]
		if e.Priority != domain.LowPriority || len(e.Cells) != 4 {
			t.Fatalf("%s %+v", role, e)
		}
		for _, c := range e.Cells {
			if c.X < 10 {
				t.Fatalf("%s indoors: %+v", role, e.Cells)
			}
			if other, dup := taken[c]; dup {
				t.Fatalf("%s overlaps %s at %v", role, other, c)
			}
			taken[c] = role
		}
	}
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

// A role with a zone, or with nothing waiting, is never created; dumps
// wait on a known room census.
func TestStockpileCreateSkipsCoveredRolesAndUnknownRooms(t *testing.T) {
	r := stockpileCreateRequest()
	r.Zones = append(r.Zones, StockpileZone{ID: "Zone_2", Role: domain.ApparelRole, Cells: []domain.Cell{{X: 6, Z: 6}}, Filter: domain.ApparelFilter(), Priority: domain.PreferredPriority})
	r.Needs = map[string]int{domain.ApparelRole: 5, domain.CorpseDumpRole: 2}
	r.Rooms = domain.Unknown[[]Room]()
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind == StockpileCreate {
			t.Fatalf("created %+v", e)
		}
	}
}
