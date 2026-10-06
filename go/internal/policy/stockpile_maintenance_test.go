package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// stockpileRect lists a w x h rectangle's cells.
func stockpileRect(x, z, w, h int32) []domain.Cell {
	var out []domain.Cell
	for dx := int32(0); dx < w; dx++ {
		for dz := int32(0); dz < h; dz++ {
			out = append(out, domain.Cell{X: x + dx, Z: z + dz})
		}
	}
	return out
}

// stockpileField is a 40x40 open map with the given zones' cells zoned.
func stockpileField(zones ...StockpileZone) StockpileRequest {
	zoned := map[domain.Cell]bool{}
	for _, z := range zones {
		for _, c := range z.Cells {
			zoned[c] = true
		}
	}
	r := StockpileRequest{Tick: 100000, Zones: zones, Bounds: Bounds{Width: 40, Height: 40}}
	for _, c := range stockpileRect(0, 0, 40, 40) {
		r.Cells = append(r.Cells, SiteCell{Cell: c, Walkable: domain.Known(true), Things: OccupantThings(false), Zone: domain.Known(zoned[c])})
	}
	return r
}

// A full zone is left as it stands: its empty cells are its headroom, and a
// lone fragment of a role is never merged away.
func TestStockpileNeverGrowsShrinksOrMerges(t *testing.T) {
	cells := stockpileRect(10, 10, 4, 4)
	full := StockpileZone{ID: "Zone_1", Role: "general", Cells: cells, Stored: cells}
	sparse := StockpileZone{ID: "Zone_2", Role: "general", Cells: stockpileRect(20, 20, 6, 6), Stored: stockpileRect(20, 20, 1, 1)}
	frag := StockpileZone{ID: "Zone_3", Role: "general", Cells: stockpileRect(30, 30, 1, 1)}
	if review := PlanStockpileMaintenance(stockpileField(full, sparse, frag)); review.Active || !review.Known {
		t.Fatalf("review %+v", review)
	}
}

func TestStockpileRetargetsAndDeletesByRoleAndSkipsLegacy(t *testing.T) {
	general := domain.GeneralFilter()
	food := domain.FoodFilter()
	zones := []StockpileZone{
		{ID: "Zone_1", Role: "ingredients:Bench_1", Cells: stockpileRect(0, 0, 3, 3), Stored: stockpileRect(0, 0, 1, 2), Filter: general, Priority: domain.NormalPriority},
		{ID: "Zone_2", Role: "dump:worn", Cells: stockpileRect(20, 0, 3, 3), Stored: stockpileRect(20, 0, 1, 3), Filter: general, Priority: domain.LowPriority},
		{ID: "Zone_3", Cells: stockpileRect(30, 30, 3, 3), Filter: general, Priority: domain.NormalPriority},
	}
	r := stockpileField(zones...)
	r.Stores = []Store{
		{StoreSite: StoreSite{Role: "ingredients:Bench_1", Interior: Rectangle{X: 0, Z: 0, Width: 3, Height: 3}, Filter: food, Priority: domain.ImportantPriority, exact: true}},
		{StoreSite: StoreSite{Role: "dump:worn", exact: true}, Retired: true},
	}
	review := PlanStockpileMaintenance(r)
	if len(review.Edits) != 2 {
		t.Fatalf("review %+v", review)
	}
	if e := review.Edits[0]; e.Kind != StockpileDelete || e.Zone != "Zone_2" {
		t.Fatalf("delete %+v", e)
	}
	if e := review.Edits[1]; e.Kind != StockpileRetarget || e.Zone != "Zone_1" || e.Filter != food || e.Priority != domain.ImportantPriority || e.Role != "ingredients:Bench_1" {
		t.Fatalf("retarget %+v", e)
	}
	// Once applied, nothing stands.
	r.Zones[0].Filter, r.Zones[0].Priority = food, domain.ImportantPriority
	r.Zones = r.Zones[:1]
	if review := PlanStockpileMaintenance(r); review.Active {
		t.Fatalf("applied review %+v", review)
	}
}

func TestStockpileReviewCountsZonesByRoleKind(t *testing.T) {
	cell := func(x int32) domain.Cell { return domain.Cell{X: x} }
	zones := []StockpileZone{
		{ID: "a", Role: "general", Cells: []domain.Cell{cell(0), cell(1)}, Stored: []domain.Cell{cell(0)}},
		{ID: "b", Role: "general", Cells: []domain.Cell{cell(2)}},
		{ID: "c", Role: "medicine:r7", Cells: []domain.Cell{cell(3)}},
		{ID: "d", Cells: []domain.Cell{cell(4)}},
	}
	want := []StockpileRoleCount{{"general", 2, 3, 1}, {"medicine", 1, 1, 0}, {"untagged", 1, 1, 0}}
	if got := StockpileRoleCounts(zones); !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
}
