package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shelterView() (StoreView, PlannedRoom) {
	shelter := PlannedRoom{Role: PlannedShelter, Region: RectRegion(Rectangle{X: 4, Z: 4, Width: 5, Height: 4}), Door: domain.Cell{X: 6, Z: 8}}
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

// While the colony has no storage room the shelter's whole floor is a Low
// priority general store, created before any wall stands.
func TestShelterStoreCoversTheShelterFloor(t *testing.T) {
	t.Parallel()
	view, shelter := shelterView()
	edit, ok := storeCreates(view)[domain.ShelterRole]
	if !ok || edit.Priority != domain.LowPriority || edit.Filter != domain.GeneralFilter() || len(edit.Cells()) != int(shelter.Region.Bounds().Width*shelter.Region.Bounds().Height) || !withinRect(edit.Cells(), shelter.Region.Bounds()) {
		t.Fatalf("shelter store: %+v", edit)
	}
	if live, retired := shelterStores(view); live != 1 || retired != 0 {
		t.Fatal(live, retired)
	}
	if live, retired := shelterStores(StoreView{}); live+retired != 0 {
		t.Fatal("an unread layout declared a shelter store")
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
	view.Layout.Rooms = append(view.Layout.Rooms, PlannedRoom{Role: PlannedShelter, Region: RectRegion(Rectangle{X: 4, Z: 4, Width: 5, Height: 4})})
	rank := map[domain.StockpilePriority]int{domain.LowPriority: 0, domain.NormalPriority: 1, domain.PreferredPriority: 2, domain.ImportantPriority: 3, domain.CriticalPriority: 4}
	for _, s := range DeclareStores(view).Stores {
		if s.Role != domain.ShelterRole && rank[s.Priority] < rank[domain.LowPriority] {
			t.Fatal(s.Role, s.Priority)
		}
		if s.Role == domain.ShelterRole && s.Priority != domain.LowPriority {
			t.Fatal("shelter priority", s.Priority)
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
