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
	r := StockpileRequest{Tick: 100000, Zones: zones, Bounds: Bounds{Width: 40, Height: 40}, Colonists: domain.Known(int64(3))}
	for _, c := range stockpileRect(0, 0, 40, 40) {
		r.Cells = append(r.Cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(zoned[c])})
	}
	return r
}

func TestStockpileGrowsWhenNearlyFullOntoAdjacentOpenCells(t *testing.T) {
	cells := stockpileRect(10, 10, 4, 4)
	zone := StockpileZone{ID: "Zone_1", Role: "general", Cells: cells, Stored: cells[:14]}
	review := PlanStockpileMaintenance(stockpileField(zone))
	if !review.Active || len(review.Edits) != 1 {
		t.Fatalf("review %+v", review)
	}
	e := review.Edits[0]
	if e.Kind != StockpileGrow || len(e.Cells) != StockpileMinCells || e.Hauls != len(e.Cells) {
		t.Fatalf("grow %+v", e)
	}
	own := map[domain.Cell]bool{}
	for _, c := range cells {
		own[c] = true
	}
	for _, c := range e.Cells {
		adjacent := false
		for _, n := range stockpileNeighbours(c) {
			adjacent = adjacent || own[n]
		}
		if own[c] || !adjacent {
			t.Fatalf("grow cell %v not on the edge", c)
		}
	}
	// A full zone (overflow on the floor) grows twice as much.
	zone.Stored = cells
	if review := PlanStockpileMaintenance(stockpileField(zone)); len(review.Edits) != 1 || len(review.Edits[0].Cells) != 2*StockpileMinCells {
		t.Fatalf("full review %+v", review)
	}
	// Half full: nothing to do.
	zone.Stored = cells[:8]
	if review := PlanStockpileMaintenance(stockpileField(zone)); review.Active || !review.Known {
		t.Fatalf("half review %+v", review)
	}
}

func TestStockpileShrinksOnlyAfterSittingMostlyEmpty(t *testing.T) {
	cells := stockpileRect(10, 10, 6, 6)
	zone := StockpileZone{ID: "Zone_1", Cells: cells, Stored: []domain.Cell{{X: 12, Z: 12}, {X: 13, Z: 13}}}
	r := stockpileField(zone)
	if review := PlanStockpileMaintenance(r); review.Active {
		t.Fatalf("shrank without a low history %+v", review)
	}
	r.Zones[0].LowSince = r.Tick - StockpileShrinkAfter + 1
	if review := PlanStockpileMaintenance(r); review.Active {
		t.Fatalf("shrank too early %+v", review)
	}
	r.Zones[0].LowSince = r.Tick - StockpileShrinkAfter
	review := PlanStockpileMaintenance(r)
	if len(review.Edits) != 1 || review.Edits[0].Kind != StockpileShrink || review.Edits[0].Hauls != 0 {
		t.Fatalf("review %+v", review)
	}
	removed := map[domain.Cell]bool{}
	for _, c := range review.Edits[0].Cells {
		removed[c] = true
	}
	if removed[domain.Cell{X: 12, Z: 12}] || removed[domain.Cell{X: 13, Z: 13}] {
		t.Fatal("shrink removed a used cell")
	}
	remaining := map[domain.Cell]bool{}
	for _, c := range cells {
		if !removed[c] {
			remaining[c] = true
		}
	}
	if len(remaining) != StockpileMinCells || !stockpileContiguous(remaining) {
		t.Fatalf("remaining %v", remaining)
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
	r.Roles = func(role string) (StockpileRoleState, bool) {
		switch role {
		case "ingredients:Bench_1":
			return StockpileRoleState{Filter: food, Priority: domain.ImportantPriority}, true
		case "dump:worn":
			return StockpileRoleState{Retired: true}, true
		case "":
			t.Fatal("a legacy zone's role was resolved")
		}
		return StockpileRoleState{}, false
	}
	review := PlanStockpileMaintenance(r)
	if len(review.Edits) != 2 {
		t.Fatalf("review %+v", review)
	}
	if e := review.Edits[0]; e.Kind != StockpileDelete || e.Zone != "Zone_2" || e.Hauls != 3 {
		t.Fatalf("delete %+v", e)
	}
	if e := review.Edits[1]; e.Kind != StockpileRetarget || e.Zone != "Zone_1" || e.Filter != food || e.Priority != domain.ImportantPriority || e.Role != "ingredients:Bench_1" || e.Hauls != 2 {
		t.Fatalf("retarget %+v", e)
	}
	// Once applied, nothing stands.
	r.Zones[0].Filter, r.Zones[0].Priority = food, domain.ImportantPriority
	r.Zones = r.Zones[:1]
	if review := PlanStockpileMaintenance(r); review.Active {
		t.Fatalf("applied review %+v", review)
	}
}

func TestStockpileMergesAFragmentIntoItsSibling(t *testing.T) {
	big := StockpileZone{ID: "Zone_1", Role: "general", Cells: stockpileRect(0, 0, 4, 4), Stored: stockpileRect(0, 0, 1, 4)}
	frag := StockpileZone{ID: "Zone_2", Role: "general", Cells: stockpileRect(20, 20, 2, 2), Stored: stockpileRect(20, 20, 1, 1)}
	other := StockpileZone{ID: "Zone_3", Role: "dump:worn", Cells: stockpileRect(30, 30, 1, 2)}
	review := PlanStockpileMaintenance(stockpileField(big, frag, other))
	if len(review.Edits) != 1 || review.Edits[0].Kind != StockpileMerge || review.Edits[0].Zone != "Zone_2" || review.Edits[0].Into != "Zone_1" || review.Edits[0].Hauls != 1 {
		t.Fatalf("review %+v", review)
	}
}

// The haul budget admits edits in urgency order until the next would
// overrun it; the first edit always lands.
func TestStockpileEditsAreRateLimitedByHauls(t *testing.T) {
	var zones []StockpileZone
	for i := int32(0); i < 3; i++ {
		cells := stockpileRect(i*12, 5, 5, 5)
		zones = append(zones, StockpileZone{ID: "Zone_" + string(rune('A'+i)), Role: "general", Cells: cells, Stored: cells})
	}
	r := stockpileField(zones...)
	r.Colonists = domain.Known(int64(1))
	review := PlanStockpileMaintenance(r)
	if review.Budget != StockpileHaulsPerColonist || len(review.Edits) != 1 || review.Deferred != 2 || review.Edits[0].Zone != "Zone_A" {
		t.Fatalf("review %+v", review)
	}
	r.Colonists = domain.Known(int64(5))
	if review := PlanStockpileMaintenance(r); len(review.Edits) != 3 || review.Deferred != 0 {
		t.Fatalf("roomy review %+v", review)
	}
	r.Colonists = domain.Unknown[int64]()
	if review := PlanStockpileMaintenance(r); review.Known || review.Active {
		t.Fatalf("unknown colonists review %+v", review)
	}
}

func TestStockpileShrinkKeepsFoodZoneAtStorageMinimum(t *testing.T) {
	zone := StockpileZone{ID: "Zone_1", Cells: stockpileRect(10, 10, 3, 3), Filter: domain.FoodFilter()}
	r := stockpileField(zone)
	r.Zones[0].LowSince = r.Tick - StockpileShrinkAfter
	if review := PlanStockpileMaintenance(r); review.Active {
		t.Fatalf("shrank a food zone below the storage minimum %+v", review)
	}
}

// Role-less zones are the resource and chunk planners'; folding a wood zone
// into a steel one deleted its cells, and the planner raised another the next
// pass (#1581).
func TestStockpileMergeNeverFoldsDifferentFilters(t *testing.T) {
	wood, _ := domain.AllowOnlyFilter([]string{"WoodLog"})
	steel, _ := domain.AllowOnlyFilter([]string{"Steel"})
	big := StockpileZone{ID: "Zone_1", Cells: stockpileRect(0, 0, 4, 4), Filter: steel}
	frag := StockpileZone{ID: "Zone_2", Cells: stockpileRect(20, 20, 1, 1), Filter: wood}
	if review := PlanStockpileMaintenance(stockpileField(big, frag)); review.Active {
		t.Fatalf("merged a wood fragment into a steel zone %+v", review)
	}
	frag.Filter = steel
	if review := PlanStockpileMaintenance(stockpileField(big, frag)); len(review.Edits) != 1 || review.Edits[0].Kind != StockpileMerge {
		t.Fatalf("same-filter fragment not merged %+v", review)
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
	if got := stockpileRoleCounts(zones); !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
}
