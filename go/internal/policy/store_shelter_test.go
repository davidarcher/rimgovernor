package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shelterView() (StoreView, PlannedRoom) {
	shelter := PlannedRoom{Role: PlannedShelter, Region: RectRegion(Rectangle{X: 4, Z: 4, Width: 5, Height: 4}), Door: domain.Cell{X: 6, Z: 8}, DoorRot: domain.North}
	view := StoreView{Bounds: Bounds{Width: 40, Height: 40}, Cells: unroofedGround(), Layout: &LayoutPlan{Rooms: []PlannedRoom{shelter}}}
	return view, shelter
}

func shelterStores(view StoreView) (live, retired int) {
	for _, s := range DeclareStores(view).Stores {
		if s.Role != domain.ShelterRole {
			continue
		}
		if s.Retired {
			retired++
		} else {
			live++
		}
	}
	return live, retired
}

// While the colony has no storage room the shelter's starter-storage patch is a
// Low priority general store, created before any wall stands.
func TestShelterStoreCoversTheStarterStoragePatch(t *testing.T) {
	t.Parallel()
	view, shelter := shelterView()
	shell, err := shelter.Footprint()
	if err != nil {
		t.Fatal(err)
	}
	patch := starterStorage(shell)
	edit, ok := storeCreates(view)[domain.ShelterRole]
	if !ok || edit.Priority != domain.LowPriority || edit.Filter != domain.GeneralFilter() || len(edit.Cells()) != 9 || !withinRect(edit.Cells(), patch) {
		t.Fatalf("shelter store: %+v, patch %+v", edit, patch)
	}
	if live, retired := shelterStores(view); live != 1 || retired != 0 {
		t.Fatal(live, retired)
	}
	if live, retired := shelterStores(StoreView{}); live+retired != 0 {
		t.Fatal("an unread layout declared a shelter store")
	}
}

// No template piece (bunks, crafting, both campfires) of the shelter room lies
// on the store: the placement search treats zoned cells as unplaceable.
func TestShelterStoreLeavesEveryTemplateSlotFree(t *testing.T) {
	t.Parallel()
	room := bunkRoom()
	store := map[domain.Cell]bool{}
	for _, s := range DeclareStores(StoreView{Layout: &LayoutPlan{Rooms: []PlannedRoom{room}}}).Stores {
		if s.Role == domain.ShelterRole && !s.Retired {
			for _, c := range rectCells(s.Interior) {
				store[c] = true
			}
		}
	}
	if len(store) != 9 {
		t.Fatalf("shelter store cells: %d", len(store))
	}
	interior, ok := ShelterRoom(room, nil, testShapes, 8, 2, 0, nil)
	if !ok {
		t.Fatal("no shelter room")
	}
	plan, ok := PlanInterior(interior, InteriorPieceDef{})
	if !ok || len(plan.Pieces) == 0 {
		t.Fatal("no interior plan")
	}
	for _, piece := range plan.Pieces {
		for _, c := range rectCells(piece.Rect) {
			if store[c] {
				t.Fatalf("piece %s covers store cell %v", piece.Slot, c)
			}
		}
	}
}

// A standing storage room or freezer retires the store, which deletes the zone
// so its contents haul to the better stores; so does a plan with no shelter.
func TestShelterStoreRetiresOnceAStorageRoomStands(t *testing.T) {
	t.Parallel()
	for _, role := range []PlannedRole{PlannedStorage, PlannedFreezer} {
		view, shelter := shelterView()
		room := PlannedRoom{Role: role, Region: RectRegion(Rectangle{X: 20, Z: 4, Width: 5, Height: 5}), Door: domain.Cell{X: 22, Z: 3}}
		view.Layout.Rooms = append(view.Layout.Rooms, room)
		view.Rooms = &RoomObservation{}
		zone := StockpileZone{ID: "Zone_1", Role: domain.ShelterRole, Cells: rectCells(shelter.Region.Bounds()), Filter: domain.GeneralFilter(), Priority: domain.LowPriority}
		view.Zones = []StockpileZone{zone}
		if live, retired := shelterStores(view); live != 1 || retired != 0 {
			t.Fatalf("%s planned, not standing: live %d retired %d", role, live, retired)
		}
		view.Rooms.Rooms = []Room{{ID: "r", Enclosed: domain.Known(true), Cells: rectCells(room.Region.Bounds())}}
		if live, retired := shelterStores(view); live != 0 || retired != 1 {
			t.Fatalf("%s standing: live %d retired %d", role, live, retired)
		}
		review := PlanStockpileMaintenance(StockpileRequest{Tick: 1, Bounds: view.Bounds, Cells: view.Cells, Zones: view.Zones, Stores: DeclareStores(view).Stores})
		deleted := false
		for _, e := range review.Edits {
			deleted = deleted || e.Kind == StockpileDelete && e.Zone == zone.ID
			if e.Kind == StockpileCreate && e.Role == domain.ShelterRole {
				t.Fatalf("%s standing: the shelter zone was recreated", role)
			}
		}
		if !deleted {
			t.Fatalf("%s standing: the shelter zone stayed: %+v", role, review.Edits)
		}
	}
	view, _ := shelterView()
	view.Layout.Rooms = nil
	if live, retired := shelterStores(view); live != 0 || retired != 1 {
		t.Fatal("a plan without the shelter keeps its store:", live, retired)
	}
}

// Every other store outranks the shelter floor: nothing sits below Low.
func TestShelterStoreIsTheLowestPriority(t *testing.T) {
	t.Parallel()
	view, _ := freezerView()
	view.Layout.Rooms = append(view.Layout.Rooms, PlannedRoom{Role: PlannedShelter, Region: RectRegion(Rectangle{X: 4, Z: 4, Width: 5, Height: 4}), Door: domain.Cell{X: 6, Z: 8}, DoorRot: domain.North})
	sawShelter := false
	defer func() {
		if !sawShelter {
			t.Error("no shelter store declared")
		}
	}()
	rank := map[domain.StockpilePriority]int{domain.LowPriority: 0, domain.NormalPriority: 1, domain.PreferredPriority: 2, domain.ImportantPriority: 3, domain.CriticalPriority: 4}
	for _, s := range DeclareStores(view).Stores {
		if s.Role != domain.ShelterRole && rank[s.Priority] < rank[domain.LowPriority] {
			t.Fatal(s.Role, s.Priority)
		}
		if s.Role == domain.ShelterRole {
			sawShelter = true
			if s.Priority != domain.LowPriority {
				t.Fatal("shelter priority", s.Priority)
			}
		}
	}
	freezerBeatsShelter := false
	for _, s := range DeclareStores(view).Stores {
		freezerBeatsShelter = freezerBeatsShelter || stockpileRolePrefix(s.Role) == domain.PerishablesRolePrefix[:len(domain.PerishablesRolePrefix)-1] && rank[s.Priority] > rank[domain.LowPriority]
	}
	if !freezerBeatsShelter {
		t.Fatal("the freezer does not outrank the shelter store")
	}
}
