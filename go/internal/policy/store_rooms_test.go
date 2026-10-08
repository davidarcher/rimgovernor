package policy

import (
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func storageRooms(plan LayoutPlan) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range plan.AllRooms() {
		if r.Role == PlannedStorage {
			out = append(out, r)
		}
	}
	return out
}

// A demanded room no core slot takes is an error naming the room, for the
// storage and the gear rooms alike (#1799); a placed one is not.
func TestUnplaceableRoomDemandFailsLoudly(t *testing.T) {
	t.Parallel()
	plan := gearTestPlan()
	if _, added, err := growStorageRooms(plan, RoomDemand{Storage: 2}, nil); !added || err != nil {
		t.Fatalf("placed room added=%v err=%v", added, err)
	}
	plan.Zones = nil
	if _, added, err := growStorageRooms(plan, RoomDemand{Storage: 2}, nil); added || err == nil || !strings.Contains(err.Error(), "storage room") {
		t.Fatalf("storage added=%v err=%v", added, err)
	}
	if _, added, err := growGearRooms(plan, RoomDemand{Armory: true}, nil); added || err == nil || !strings.Contains(err.Error(), "armory room") {
		t.Fatalf("armory added=%v err=%v", added, err)
	}
}

func TestStorageRoomAddedOnDemandKeepsTheCore(t *testing.T) {
	t.Parallel()
	plan := gearTestPlan()
	core, _ := plan.Core()
	if same, added, _ := growStorageRooms(plan, RoomDemand{}, nil); added || len(same.Rooms) != len(plan.Rooms) {
		t.Fatal("room added with no demand")
	}
	if same, added, _ := growStorageRooms(plan, RoomDemand{Storage: 1}, nil); added || len(same.Rooms) != len(plan.Rooms) {
		t.Fatal("a standing storage room answers a demand for one")
	}
	grown, added, _ := growStorageRooms(plan, RoomDemand{Storage: 2}, nil)
	if !added || len(grown.Rooms) != len(plan.Rooms)+1 {
		t.Fatalf("added=%v rooms %d -> %d", added, len(plan.Rooms), len(grown.Rooms))
	}
	for i := range plan.Rooms {
		if !grown.Rooms[i].Same(plan.Rooms[i]) {
			t.Fatalf("room %d moved", i)
		}
	}
	rooms := storageRooms(grown)
	if len(rooms) != 2 || rooms[1].Interior.Width*rooms[1].Interior.Height != coreRoomSize[PlannedStorage][0]*coreRoomSize[PlannedStorage][1] {
		t.Fatalf("storage rooms %+v", rooms)
	}
	if after, _ := grown.Core(); after != core {
		t.Fatalf("core moved %v -> %v", core, after)
	}
	if _, err := CheckRoutes(grown); err != nil {
		t.Fatal(err)
	}
	if owed := StorageRoomsOwed(grown, RoomDemand{Storage: 2}); owed != 0 {
		t.Fatalf("owed %d once the room is planned", owed)
	}
	if again, added, _ := growStorageRooms(grown, RoomDemand{Storage: 2}, nil); added || len(again.Rooms) != len(grown.Rooms) {
		t.Fatal("a planned room answers its demand for good")
	}
}

// storeColony is two 3x3 storage rooms, the second planned only when two is
// set, standing when stands is set, each with a warehouse zone over its
// whole room that is full when full is set.
func storeRequest(rooms int, stands int, zones ...StockpileZone) StoreView {
	var cells []SiteCell
	layout := LayoutPlan{}
	var census RoomObservation
	for i := range rooms {
		in := Rectangle{X: int32(10 + 10*i), Z: 10, Width: 3, Height: 3}
		layout.Rooms = append(layout.Rooms, PlannedRoom{Role: PlannedStorage, Interior: in, Door: domain.Cell{X: in.X + 1, Z: 9}})
		if i < stands {
			census.Rooms = append(census.Rooms, Room{ID: []string{"first", "second"}[i], Enclosed: domain.Known(true), Cells: rectCells(in)})
		}
	}
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			roofed := z >= 10 && z < 13 && (x >= 10 && x < 13 || x >= 20 && x < 23)
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: OccupantThings(false), Zone: domain.Known(false), Roofed: domain.Known(roofed), Indoors: domain.Known(roofed), StorageEmpty: domain.Known(true)})
		}
	}
	return StoreView{Bounds: Bounds{Width: 40, Height: 40}, Cells: cells, Layout: &layout, Rooms: &census, Zones: zones}
}

func warehouseZone(id, role string, in Rectangle, stored int) StockpileZone {
	cells := rectCells(in)
	return StockpileZone{ID: id, Role: role, Cells: cells, Stored: cells[:stored], Filter: domain.GeneralFilter(), Priority: domain.LowPriority}
}

// storageDemand is the room demand layout reads: the declared stores' answer.
func storageDemand(req StoreView) RoomDemand {
	return DeclareStores(req).Apply(RoomDemand{})
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

// stockpileOf is the maintenance request for a storage view: its declared
// stores over the view's ground and zones, the waste dump already standing.
func stockpileOf(req StoreView) StockpileRequest {
	r := StockpileRequest{Tick: 100000}
	r.Zones = []StockpileZone{{ID: "Zone_dump", Role: domain.DumpRole, Cells: []domain.Cell{{X: 30, Z: 20}}, Filter: domain.DumpFilter(), Priority: domain.LowPriority}}
	r.Cells, r.Bounds = req.Cells, req.Bounds
	r.Zones = append(slices.Clone(r.Zones), req.Zones...)
	r.Stores = DeclareStores(req).Stores
	return r
}

func TestStorageRoomDemandFollowsTheFillThreshold(t *testing.T) {
	t.Parallel()
	first := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	// 8 of 9 cells used is over the threshold; 7 of 9 is under it.
	if got := storageDemand(storeRequest(1, 1, warehouseZone("a", domain.GeneralRole, first, 8))).Storage; got != 2 {
		t.Fatalf("full warehouse wants %d rooms, want 2", got)
	}
	if got := storageDemand(storeRequest(1, 1, warehouseZone("a", domain.GeneralRole, first, 7))).Storage; got != 0 {
		t.Fatalf("a warehouse under the threshold wants %d rooms", got)
	}
	// The zone stands on open ground before its room: its fill is the demand.
	if got := storageDemand(storeRequest(1, 0, warehouseZone("a", domain.GeneralRole, first, 9))).Storage; got != 2 {
		t.Fatalf("a full zone in an unbuilt room wants %d rooms, want 2", got)
	}
	if got := storageDemand(storeRequest(1, 1)).Storage; got != 0 {
		t.Fatalf("no warehouse zone yet, wants %d", got)
	}
}

func TestSecondWarehouseFollowsTheSecondRoom(t *testing.T) {
	t.Parallel()
	first, second := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}, Rectangle{X: 20, Z: 10, Width: 3, Height: 3}
	full := warehouseZone("a", domain.GeneralRole, first, 9)
	// The second room is planned but holds no zone yet: no more demand.
	req := storeRequest(2, 1, full)
	if got := storageDemand(req).Storage; got != 0 {
		t.Fatalf("a planned room answers the demand: %d", got)
	}
	// Both warehouses stand with a zone and are full: a third room.
	zone := warehouseZone("b", domain.GeneralRole, second, 9)
	if got := storageDemand(storeRequest(2, 2, full, zone)).Storage; got != 3 {
		t.Fatalf("both warehouses full want %d rooms, want 3", got)
	}
	zone.Stored = zone.Stored[:2]
	if got := storageDemand(storeRequest(2, 2, full, zone)).Storage; got != 0 {
		t.Fatalf("second warehouse has room, wants %d", got)
	}
}

// The warehouse covers the whole interior of its planned room: created on
// open ground at plan time (the room need not stand), Low priority, on the
// indoor-only filter without the burnable, and the opening store is not raised
// beside it.
func TestWarehouseIsOneZoneOverItsWholeRoom(t *testing.T) {
	t.Parallel()
	req := storeRequest(1, 0)
	created := createdRoles(PlanStockpileMaintenance(stockpileOf(req)))
	zone, ok := created[domain.GeneralRole]
	if !ok || len(zone.Cells()) != 9 || zone.Priority != domain.LowPriority || zone.Filter != domain.GeneralFilter() || zone.Filter.Base() != domain.BaseIndoorOnly {
		t.Fatalf("created %+v", created)
	}
	if !slices.Contains(zone.Filter.Disallow(), domain.SpecialFilter(domain.BurnableFilterDef)) {
		t.Fatalf("warehouse filter %+v allows the burnable", zone.Filter)
	}
}

// A full warehouse stays stable; a partial zone fills cleared room cells.
func TestWarehouseFillsClearedRoom(t *testing.T) {
	t.Parallel()
	first := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	for _, stored := range []int{0, 4, 9} {
		zone := warehouseZone("a", domain.GeneralRole, first, stored)
		if edits := PlanStockpileMaintenance(stockpileOf(storeRequest(1, 1, zone))).Edits; len(edits) != 0 {
			t.Fatalf("%d stored: %+v", stored, edits)
		}
	}
	// A smaller standing zone grows onto the remaining room cells.
	small := warehouseZone("a", domain.GeneralRole, first, 4)
	small.Cells, small.Stored = small.Cells[:4], small.Stored[:4]
	if edits := PlanStockpileMaintenance(stockpileOf(storeRequest(1, 1, small))).Edits; len(edits) != 1 || edits[0].Kind != StockpileGrow || edits[0].Zone != "a" || len(edits[0].Cells()) != 5 {
		t.Fatalf("growth: %+v", edits)
	}
}

// A standing second room gets its warehouse zone, over its whole room.
func TestSecondWarehouseZoneIsCreated(t *testing.T) {
	t.Parallel()
	first := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	req := storeRequest(2, 2, warehouseZone("a", domain.GeneralRole, first, 9))
	zone, ok := createdRoles(PlanStockpileMaintenance(stockpileOf(req)))[domain.GeneralRole]
	if !ok || len(zone.Cells()) != 9 || zone.Priority != domain.LowPriority {
		t.Fatalf("created %+v", zone)
	}
	for _, c := range zone.Cells() {
		if c.X < 20 || c.X > 22 || c.Z < 10 || c.Z > 12 {
			t.Fatalf("second warehouse outside its room: %v", zone.Cells())
		}
	}
}

// A zone in a further warehouse room is kept (#1798).
func TestFurtherWarehouseZoneIsKeptAndCountsAsGeneral(t *testing.T) {
	t.Parallel()
	second := Rectangle{X: 20, Z: 10, Width: 3, Height: 3}
	req := storeRequest(2, 2, warehouseZone("b", domain.GeneralRole, second, 9))
	r := stockpileOf(req)
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind == StockpileDelete {
			t.Fatalf("a zone in a further warehouse room was moved: %+v", e)
		}
	}
}

// A dug warehouse gets no zone until its whole interior is open.
func TestDugWarehouseWaitsForItsInterior(t *testing.T) {
	t.Parallel()
	req := storeRequest(1, 0)
	for i, c := range req.Cells {
		if c.Cell == (domain.Cell{X: 11, Z: 11}) {
			req.Cells[i].Walkable = domain.Known(false)
			req.Cells[i].SetNaturalRock(true)
		}
	}
	if created := createdRoles(PlanStockpileMaintenance(stockpileOf(req))); len(created) != 0 && created[domain.GeneralRole].Role != "" {
		t.Fatalf("zone over a half-dug room: %+v", created)
	}
	for i, c := range req.Cells {
		if c.Cell == (domain.Cell{X: 11, Z: 11}) {
			req.Cells[i].Walkable = domain.Known(true)
			req.Cells[i].SetOccupied(false)
		}
	}
	if created := createdRoles(PlanStockpileMaintenance(stockpileOf(req))); len(created[domain.GeneralRole].Cells()) != 9 {
		t.Fatalf("no zone over the open room: %+v", created)
	}
}
