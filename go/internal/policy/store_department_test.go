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

func (testOwner) Department() Department                   { return DepartmentMilitary }
func (o testOwner) Stores(StorageRequest) []Store          { return o.stores }
func (o testOwner) RoomDemand(v StorageRequest) RoomDemand { return DeclaredDemand(v, o.stores) }

// A department's capacity reading reaches layout: a full armory store asks
// for the room, an idle one clears it, and a declared room overrides the
// fill-based reading while an undeclared one keeps it.
func TestDepartmentRoomDemandReachesLayout(t *testing.T) {
	t.Parallel()
	armory := testStore()
	armory.Further = PlannedArmory
	full := declaredZone("Zone_food", testStoreRoom, 9)
	owner := testOwner{stores: []Store{armory}}
	d := declareStores([]StoreOwner{owner}, StorageRequest{Zones: []StockpileZone{full}})
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
	d = declareStores([]StoreOwner{owner}, StorageRequest{Zones: []StockpileZone{idle}})
	if got := d.Apply(RoomDemand{Armory: true}); got.Armory {
		t.Fatalf("an idle declared store keeps the old armory demand: %+v", got)
	}
}

func TestDeclaredStorageDemandWaitsOnAStandingRoom(t *testing.T) {
	t.Parallel()
	store := testStore()
	store.Further = PlannedStorage
	if d := DeclaredDemand(StorageRequest{}, []Store{store}); d.Storage != 0 || d.StorageIdle || d.Known {
		t.Fatalf("a room with no zone is a wait: %+v", d)
	}
	if d := DeclaredDemand(StorageRequest{Zones: []StockpileZone{declaredZone("z", testStoreRoom, 1)}}, []Store{store}); !d.StorageIdle || d.Storage != 0 {
		t.Fatalf("space to spare is idle: %+v", d)
	}
	if d := DeclaredDemand(StorageRequest{Zones: []StockpileZone{declaredZone("z", testStoreRoom, 9)}}, []Store{store}); d.StorageIdle || d.Storage != 1 {
		t.Fatalf("a full store asks for one more room: %+v", d)
	}
}
