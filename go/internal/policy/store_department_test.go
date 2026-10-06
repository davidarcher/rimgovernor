package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var testStoreRoom = Rectangle{X: 4, Z: 4, Width: 3, Height: 3}

func testStore() Store {
	return Store{StoreSite: StoreSite{Role: domain.FoodRole, Interior: testStoreRoom, Filter: domain.FoodFilter(), Priority: domain.PreferredPriority}}
}

func declaredZone(id string, room Rectangle, used int) StockpileZone {
	cells := rectCells(room)
	return StockpileZone{ID: id, Role: domain.FoodRole, Cells: cells, Stored: cells[:used], Filter: domain.FoodFilter(), Priority: domain.PreferredPriority}
}

func editKinds(r StockpileRequest) []StockpileEditKind {
	var out []StockpileEditKind
	for _, e := range PlanStockpileMaintenance(r).Edits {
		out = append(out, e.Kind)
	}
	return out
}

// stockpileCreateRequest is a 20x20 map: x<10 an indoor roofed room
// holding a 2x2 general store at (2,2), x>=10 open ground.
func stockpileCreateRequest() StockpileRequest {
	r := StockpileRequest{Tick: 100, Bounds: Bounds{Width: 20, Height: 20}}
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

func TestDeclaredStoreIsCreatedOverItsWholeRoom(t *testing.T) {
	t.Parallel()
	r := stockpileCreateRequest()
	r.Zones = nil
	r.Stores = []Store{testStore()}
	edits := PlanStockpileMaintenance(r).Edits
	if len(edits) != 1 || edits[0].Kind != StockpileCreate || len(edits[0].Cells) != 9 || edits[0].Priority != domain.PreferredPriority {
		t.Fatalf("edits %+v, want one 9-cell create at the store's priority", edits)
	}
}

func TestDeclaredStoreIsRetargetedNeverResized(t *testing.T) {
	t.Parallel()
	r := stockpileCreateRequest()
	zone := declaredZone("Zone_food", testStoreRoom, 9)
	zone.Priority = domain.NormalPriority
	r.Zones = []StockpileZone{zone}
	r.Stores = []Store{testStore()}
	edits := PlanStockpileMaintenance(r).Edits
	if len(edits) != 1 || edits[0].Kind != StockpileRetarget || edits[0].Priority != domain.PreferredPriority {
		t.Fatalf("a full zone with stale settings: %+v, want only a retarget (no grow)", edits)
	}
	r.Zones[0].Priority = domain.PreferredPriority
	r.Zones[0].Stored = nil
	if kinds := editKinds(r); len(kinds) != 0 {
		t.Fatalf("a mostly empty zone at its settings: %v, want no shrink", kinds)
	}
}

func TestDeclaredStoreIsDeletedOnlyWhenRetired(t *testing.T) {
	t.Parallel()
	r := stockpileCreateRequest()
	r.Zones = []StockpileZone{declaredZone("Zone_food", testStoreRoom, 2)}
	if kinds := editKinds(r); len(kinds) != 0 {
		t.Fatalf("undeclared role is left alone: %v", kinds)
	}
	r.Stores = []Store{testStore()}
	r.Stores[0].Retired = true
	if kinds := editKinds(r); len(kinds) != 1 || kinds[0] != StockpileDelete {
		t.Fatalf("retired store: %v, want one delete", kinds)
	}
}

func TestDeclaredStoreMoveCreatesBeforeItDeletes(t *testing.T) {
	t.Parallel()
	r := stockpileCreateRequest()
	r.Zones = []StockpileZone{declaredZone("Zone_food", Rectangle{X: 12, Z: 12, Width: 3, Height: 3}, 1)}
	r.Stores = []Store{testStore()}
	if kinds := editKinds(r); len(kinds) != 2 || kinds[0] != StockpileCreate || kinds[1] != StockpileDelete {
		t.Fatalf("edits %v, want create then delete", kinds)
	}
}

func TestDeclaredStoresNeverOverlap(t *testing.T) {
	t.Parallel()
	r := stockpileCreateRequest()
	r.Zones = nil
	a, b := testStore(), testStore()
	b.Role = domain.GeneralRole
	r.Stores = []Store{a, b}
	seen := map[domain.Cell]bool{}
	for _, e := range PlanStockpileMaintenance(r).Edits {
		for _, c := range e.Cells {
			if seen[c] {
				t.Fatalf("cell %v claimed twice", c)
			}
			seen[c] = true
		}
	}
	if len(seen) != 9 {
		t.Fatalf("%d cells claimed; the second store has no free ground", len(seen))
	}
}

type testOwner struct{ stores []Store }

func (testOwner) Department() Department              { return DepartmentMilitary }
func (o testOwner) Stores(StoreView) []Store          { return o.stores }
func (o testOwner) RoomDemand(v StoreView) RoomDemand { return DeclaredDemand(v, o.stores) }

// A department's capacity reading reaches layout: a full armory store asks
// for the room, an idle one clears it, and a declared room overrides the
// fill-based reading while an undeclared one keeps it.
func TestDepartmentRoomDemandReachesLayout(t *testing.T) {
	t.Parallel()
	armory := testStore()
	armory.Further = PlannedArmory
	full := declaredZone("Zone_food", testStoreRoom, 9)
	owner := testOwner{stores: []Store{armory}}
	d := declareStores([]StoreOwner{owner}, StoreView{Zones: []StockpileZone{full}})
	old := RoomDemand{Wardrobe: true, Storage: 3}
	got := d.Apply(old)
	if !got.Armory || !got.Known || !got.Wardrobe || got.Storage != 3 {
		t.Fatalf("demand %+v: armory declared full, the undeclared rooms keep the old reading", got)
	}
	plan := gearTestPlan()
	if owed := GearRoomsOwed(plan, got); len(owed) == 0 {
		t.Fatal("layout owes no armory for a full declared store")
	}
	idle := declaredZone("Zone_food", testStoreRoom, 1)
	d = declareStores([]StoreOwner{owner}, StoreView{Zones: []StockpileZone{idle}})
	if got := d.Apply(RoomDemand{Armory: true}); got.Armory {
		t.Fatalf("an idle declared store keeps the old armory demand: %+v", got)
	}
}

func TestDeclaredStorageDemandWaitsOnAStandingRoom(t *testing.T) {
	t.Parallel()
	store := testStore()
	store.Further = PlannedStorage
	if d := DeclaredDemand(StoreView{}, []Store{store}); d.Storage != 0 || d.StorageIdle || d.Known {
		t.Fatalf("a room with no zone is a wait: %+v", d)
	}
	if d := DeclaredDemand(StoreView{Zones: []StockpileZone{declaredZone("z", testStoreRoom, 1)}}, []Store{store}); !d.StorageIdle || d.Storage != 0 {
		t.Fatalf("space to spare is idle: %+v", d)
	}
	if d := DeclaredDemand(StoreView{Zones: []StockpileZone{declaredZone("z", testStoreRoom, 9)}}, []Store{store}); d.StorageIdle || d.Storage != 1 {
		t.Fatalf("a full store asks for one more room: %+v", d)
	}
}

// A shelf takes its zone's desired settings (its declared store's over the
// zone's own): an unpatched shelf and one whose store's settings moved are
// patched, one already matching is left alone.
func TestStockpileShelvesFollowTheirStore(t *testing.T) {
	t.Parallel()
	r := stockpileCreateRequest()
	food := domain.FoodFilter()
	r.Stores = []Store{{StoreSite: StoreSite{Role: domain.GeneralRole, Interior: Rectangle{X: 2, Z: 2, Width: 2, Height: 2}, Filter: food, Priority: domain.ImportantPriority}}}
	r.Zones[0].Filter, r.Zones[0].Priority = food, domain.ImportantPriority
	r.Shelves = []StockpileShelf{
		{Building: "Shelf_1", Zone: "Zone_1", Cells: 2},
		{Building: "Shelf_2", Zone: "Zone_1", Cells: 2, Patched: true, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
		{Building: "Shelf_3", Zone: "Zone_1", Cells: 2, Patched: true, Filter: food, Priority: domain.ImportantPriority},
		{Building: "Shelf_4", Zone: "Zone_9", Cells: 2},
	}
	var patched []string
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind != StockpileShelfPatch || e.Filter != food || e.Priority != domain.ImportantPriority || e.Role != ShelfRole(e.Zone) {
			t.Fatalf("shelf edit %+v", e)
		}
		patched = append(patched, e.Zone)
	}
	if len(patched) != 2 || patched[0] != "Shelf_1" || patched[1] != "Shelf_2" {
		t.Fatalf("patched %v", patched)
	}
}
