package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func storageRooms(plan LayoutPlan) []LayoutRoom {
	var out []LayoutRoom
	for _, r := range plan.AllRooms() {
		if r.Role == ModuleStorage {
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
	if _, added, err := growStorageRooms(plan, RoomDemand{Storage: 2}); !added || err != nil {
		t.Fatalf("placed room added=%v err=%v", added, err)
	}
	plan.Zones = nil
	if _, added, err := growStorageRooms(plan, RoomDemand{Storage: 2}); added || err == nil || !strings.Contains(err.Error(), "storage room") {
		t.Fatalf("storage added=%v err=%v", added, err)
	}
	if _, added, err := growGearRooms(plan, RoomDemand{Armory: true}); added || err == nil || !strings.Contains(err.Error(), "armory room") {
		t.Fatalf("armory added=%v err=%v", added, err)
	}
}

func TestStorageRoomAddedOnDemandKeepsTheCore(t *testing.T) {
	t.Parallel()
	plan := gearTestPlan()
	core, _ := plan.Core()
	if same, added, _ := growStorageRooms(plan, RoomDemand{}); added || len(same.Rooms) != len(plan.Rooms) {
		t.Fatal("room added with no demand")
	}
	if same, added, _ := growStorageRooms(plan, RoomDemand{Storage: 1}); added || len(same.Rooms) != len(plan.Rooms) {
		t.Fatal("a standing storage room answers a demand for one")
	}
	grown, added, _ := growStorageRooms(plan, RoomDemand{Storage: 2})
	if !added || len(grown.Rooms) != len(plan.Rooms)+1 {
		t.Fatalf("added=%v rooms %d -> %d", added, len(plan.Rooms), len(grown.Rooms))
	}
	for i := range plan.Rooms {
		if grown.Rooms[i] != plan.Rooms[i] {
			t.Fatalf("room %d moved", i)
		}
	}
	rooms := storageRooms(grown)
	if len(rooms) != 2 || rooms[1].Interior.Width*rooms[1].Interior.Height != coreRoomSize[ModuleStorage][0]*coreRoomSize[ModuleStorage][1] {
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
	if again, added, _ := growStorageRooms(grown, RoomDemand{Storage: 2}); added || len(again.Rooms) != len(grown.Rooms) {
		t.Fatal("a planned room answers its demand for good")
	}
}

// storeColony is two 3x3 storage rooms, the second planned only when two is
// set, standing when stands is set, each with a warehouse zone over its
// whole room that is full when full is set.
func storeRequest(rooms int, stands int, zones ...StockpileZone) StorageRequest {
	var cells []SiteCell
	layout := LayoutPlan{}
	var census RoomObservation
	for i := range rooms {
		in := Rectangle{X: int32(10 + 10*i), Z: 10, Width: 3, Height: 3}
		layout.Rooms = append(layout.Rooms, LayoutRoom{Role: ModuleStorage, Interior: in, Door: domain.Cell{X: in.X + 1, Z: 9}})
		if i < stands {
			census.Rooms = append(census.Rooms, Room{ID: []string{"first", "second"}[i], Enclosed: domain.Known(true), Cells: rectCells(in)})
		}
	}
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			roofed := z >= 10 && z < 13 && (x >= 10 && x < 13 || x >= 20 && x < 23)
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(roofed), Indoors: domain.Known(roofed), StorageEmpty: domain.Known(true)})
		}
	}
	return StorageRequest{Bounds: Bounds{Width: 40, Height: 40}, Cells: cells, Layout: &layout, Rooms: &census, Zones: zones}
}

func warehouseZone(id, role string, in Rectangle, stored int) StockpileZone {
	cells := rectCells(in)
	return StockpileZone{ID: id, Role: role, Cells: cells, Stored: cells[:stored], Filter: domain.GeneralFilter(), Priority: domain.LowPriority}
}

func TestStorageRoomDemandFollowsTheFillThreshold(t *testing.T) {
	t.Parallel()
	first := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	// 8 of 9 cells used is over the threshold and the room has no cell left
	// to grow onto; 7 of 9 is under it.
	if got := PlanStorage(storeRequest(1, 1, warehouseZone("a", domain.GeneralRole, first, 8))).RoomDemand.Storage; got != 2 {
		t.Fatalf("full warehouse wants %d rooms, want 2", got)
	}
	if got := PlanStorage(storeRequest(1, 1, warehouseZone("a", domain.GeneralRole, first, 7))).RoomDemand.Storage; got != 0 {
		t.Fatalf("a warehouse under the threshold wants %d rooms", got)
	}
	if got := PlanStorage(storeRequest(1, 0, warehouseZone("a", domain.GeneralRole, first, 9))).RoomDemand.Storage; got != 0 {
		t.Fatalf("no standing room, wants %d", got)
	}
	if got := PlanStorage(storeRequest(1, 1)).RoomDemand.Storage; got != 0 {
		t.Fatalf("no warehouse zone yet, wants %d", got)
	}
}

func TestStorageRoomDemandWaitsForRoomToGrow(t *testing.T) {
	t.Parallel()
	first := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	small := warehouseZone("a", domain.GeneralRole, first, 4)
	small.Cells = small.Cells[:4]
	small.Stored = small.Stored[:4]
	if got := PlanStorage(storeRequest(1, 1, small)).RoomDemand.Storage; got != 0 {
		t.Fatalf("a full warehouse that can still grow in its room wants %d rooms", got)
	}
}

func TestSecondWarehouseFollowsTheSecondRoom(t *testing.T) {
	t.Parallel()
	first, second := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}, Rectangle{X: 20, Z: 10, Width: 3, Height: 3}
	full := warehouseZone("a", domain.GeneralRole, first, 9)
	// The second room is planned but not standing: no site, no more demand.
	req := storeRequest(2, 1, full)
	plan := PlanStorage(req)
	if plan.RoomDemand.Storage != 0 {
		t.Fatalf("a planned room answers the demand: %d", plan.RoomDemand.Storage)
	}
	for _, s := range plan.Sites {
		if s.Role != domain.GeneralRole {
			t.Fatalf("site %q before the second room stands", s.Role)
		}
	}
	// Standing: the second warehouse site exists, served by no zone yet, and
	// the planner waits on its zone before asking for a third room.
	req = storeRequest(2, 2, full)
	plan = PlanStorage(req)
	var site StockpileSite
	for _, s := range plan.Sites {
		if s.Role == domain.GeneralRole+":second" {
			site = s
		}
	}
	if site.Role == "" || site.Supersedes != domain.OpeningGeneralRole || site.Filter != domain.GeneralFilter() || site.Priority != domain.LowPriority {
		t.Fatalf("second warehouse site %+v", site)
	}
	if plan.RoomDemand.Storage != 0 {
		t.Fatalf("unserved second room still asks: %d", plan.RoomDemand.Storage)
	}
	// The zone created in the second room fills in turn: a third room.
	zone := warehouseZone("b", domain.GeneralRole+":second", second, 9)
	if got := PlanStorage(storeRequest(2, 2, full, zone)).RoomDemand.Storage; got != 3 {
		t.Fatalf("both warehouses full want %d rooms, want 3", got)
	}
	zone.Stored = zone.Stored[:2]
	if got := PlanStorage(storeRequest(2, 2, full, zone)).RoomDemand.Storage; got != 0 {
		t.Fatalf("second warehouse has room, wants %d", got)
	}
}

// A standing second room gets its warehouse zone from the shared site diff.
func TestSecondWarehouseZoneIsCreated(t *testing.T) {
	t.Parallel()
	first := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	storage := storeRequest(2, 2, warehouseZone("a", domain.GeneralRole, first, 9))
	r := freshColonyStockpiles()
	r.Cells, r.Bounds, r.Zones = storage.Cells, storage.Bounds, storage.Zones
	r.Sited = PlanStorage(storage).Sites
	created := createdRoles(PlanStockpileMaintenance(r))
	zone, ok := created[domain.GeneralRole+":second"]
	if !ok || len(zone.Cells) == 0 || zone.Priority != domain.LowPriority {
		t.Fatalf("created %+v", created)
	}
	for _, c := range zone.Cells {
		if c.X < 20 || c.X > 22 || c.Z < 10 || c.Z > 12 {
			t.Fatalf("second warehouse outside its room: %v", zone.Cells)
		}
	}
}

// Sites sharing the warehouse prefix keep a zone standing in any of their
// rooms, and the opening store is not raised beside a further warehouse
// zone (#1798).
func TestFurtherWarehouseZoneIsKeptAndCountsAsGeneral(t *testing.T) {
	t.Parallel()
	first, second := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}, Rectangle{X: 20, Z: 10, Width: 3, Height: 3}
	zone := warehouseZone("b", domain.GeneralRole+":second", second, 9)
	r := freshColonyStockpiles()
	r.Zones = []StockpileZone{zone}
	r.Sited = []StockpileSite{
		{Role: domain.GeneralRole, Room: rectCells(first), Supersedes: domain.OpeningGeneralRole},
		{Role: domain.GeneralRole + ":second", Room: rectCells(second), Supersedes: domain.OpeningGeneralRole},
	}
	if moves := stockpileSiteMoves(r); len(moves) != 0 {
		t.Fatalf("a zone in a further warehouse room moved: %+v", moves)
	}
	if _, ok := createdRoles(PlanStockpileMaintenance(r))[domain.OpeningGeneralRole]; ok {
		t.Fatal("opening store raised beside a further warehouse zone")
	}
}
