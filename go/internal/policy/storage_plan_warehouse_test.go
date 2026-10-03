package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// warehouseColony is a 40x40 map with a 9x7 storage room at (10..18,
// 10..16), roofed inside and open ground outside; standing says whether the
// room census has it enclosed. The opening food and corpse dump zones
// already stand, so the haul budget is not shared with their creation.
func warehouseColony(standing bool) StockpileRequest {
	r := freshColonyStockpiles()
	r.Cells = nil
	var room []domain.Cell
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			inside := x >= 10 && x < 19 && z >= 10 && z < 17
			if inside {
				room = append(room, domain.Cell{X: x, Z: z})
			}
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(inside), Indoors: domain.Known(inside), StorageEmpty: domain.Known(true)})
		}
	}
	r.Zones = []StockpileZone{
		{ID: "Zone_food", Role: domain.FoodRole, Cells: []domain.Cell{{X: 30, Z: 30}}, Filter: domain.FoodFilter(), Priority: domain.PreferredPriority},
		{ID: "Zone_dump", Role: domain.CorpseDumpRole, Cells: []domain.Cell{{X: 30, Z: 20}}, Filter: domain.CorpseDumpFilter(), Priority: domain.LowPriority},
	}
	layout := LayoutPlan{Rooms: []LayoutRoom{{Role: ModuleStorage, Interior: Rectangle{X: 10, Z: 10, Width: 9, Height: 7}, Door: domain.Cell{X: 14, Z: 9}}}}
	rooms := RoomObservation{Shapes: testShapes}
	if standing {
		rooms.Rooms = []Room{{ID: "store", Enclosed: domain.Known(true), Cells: room}}
	}
	r.Sited = PlanStorage(StorageRequest{Bounds: r.Bounds, Cells: r.Cells, Layout: &layout, Rooms: &rooms}).Sites
	return r
}

func createdRoles(review StockpileReview) map[string]StockpileEdit {
	out := map[string]StockpileEdit{}
	for _, e := range review.Edits {
		if e.Kind == StockpileCreate {
			out[e.Role] = e
		}
	}
	return out
}

// Before the storage room stands the opening outdoor store is raised and no
// warehouse; once it stands the warehouse is created inside it, Low priority
// and indoor-only, and the opening store is not raised again.
func TestWarehouseReplacesTheOpeningStore(t *testing.T) {
	t.Parallel()
	before := createdRoles(PlanStockpileMaintenance(warehouseColony(false)))
	if _, ok := before[domain.OpeningGeneralRole]; !ok || before[domain.GeneralRole].Role != "" {
		t.Fatalf("before the room stands: %+v", before)
	}
	after := createdRoles(PlanStockpileMaintenance(warehouseColony(true)))
	house := after[domain.GeneralRole]
	if _, ok := after[domain.OpeningGeneralRole]; ok || house.Role == "" {
		t.Fatalf("after the room stands: %+v", after)
	}
	if house.Filter != domain.GeneralFilter() || house.Filter.Base() != domain.BaseIndoorOnly || house.Priority != domain.LowPriority || len(house.Cells) != 25 {
		t.Fatalf("warehouse %+v", house)
	}
	for _, c := range house.Cells {
		if c.X < 10 || c.X > 18 || c.Z < 10 || c.Z > 16 {
			t.Fatalf("warehouse outside the storage room: %v", house.Cells)
		}
	}
}

// Once the warehouse stands the opening store is deleted, its items
// rehoming within the haul budget (the first edit always fits, so a store
// holding more than the budget still goes, alone); until then it is kept.
func TestWarehouseDeletesTheOpeningStoreOnceItStands(t *testing.T) {
	t.Parallel()
	r := warehouseColony(true)
	var store []domain.Cell
	for x := int32(2); x < 7; x++ {
		for z := int32(2); z < 7; z++ {
			store = append(store, domain.Cell{X: x, Z: z})
		}
	}
	opening := StockpileZone{ID: "Zone_1", Role: domain.OpeningGeneralRole, Cells: store, Stored: store[:20], Filter: domain.OpeningStoreFilter(), Priority: domain.NormalPriority}
	house := StockpileZone{ID: "Zone_2", Role: domain.GeneralRole, Cells: []domain.Cell{{X: 12, Z: 12}, {X: 13, Z: 12}, {X: 12, Z: 13}, {X: 13, Z: 13}}, Filter: domain.GeneralFilter(), Priority: domain.LowPriority}

	r.Zones = append(r.Zones, opening)
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind == StockpileDelete {
			t.Fatalf("opening store deleted before the warehouse stood: %+v", e)
		}
	}
	r.Zones = append(r.Zones, house)
	var deleted []StockpileEdit
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind == StockpileDelete {
			deleted = append(deleted, e)
		}
		if e.Kind == StockpileCreate && (e.Role == domain.GeneralRole || e.Role == domain.OpeningGeneralRole) {
			t.Fatalf("a general store was created again: %+v", e)
		}
	}
	if len(deleted) != 1 || deleted[0].Zone != "Zone_1" || deleted[0].Hauls != 20 {
		t.Fatalf("deletes %+v", deleted)
	}
}

// An indoor-only zone grows onto roofed floor only.
func TestWarehouseGrowsOnlyOntoRoofedFloor(t *testing.T) {
	t.Parallel()
	r := warehouseColony(true)
	var edge []domain.Cell
	for x := int32(10); x < 19; x++ {
		edge = append(edge, domain.Cell{X: x, Z: 10})
	}
	r.Zones = append(r.Zones, StockpileZone{ID: "Zone_1", Role: domain.GeneralRole, Cells: edge, Stored: edge, Filter: domain.GeneralFilter(), Priority: domain.LowPriority})
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind != StockpileGrow {
			continue
		}
		for _, c := range e.Cells {
			if c.X < 10 || c.X > 18 || c.Z < 10 || c.Z > 16 {
				t.Fatalf("warehouse grew onto open sky: %v", e.Cells)
			}
		}
		return
	}
	t.Fatal("a full warehouse did not grow")
}
