package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The incinerator is a Sanitation store declared once its walls stand: its whole
// interior at a priority above the Low dumps, refusing the not-burnable special.
func TestIncinerationOwnerDeclaresTheZoneOnceWallsStand(t *testing.T) {
	owner := incinerationOwner{}
	if owner.Department() != DepartmentSanitation {
		t.Fatal(owner.Department())
	}
	if got := owner.Stores(StorageRequest{}); len(got) != 0 {
		t.Fatal("a store before the walls stand", got)
	}
	room := incineratorRoom(Rectangle{X: 30, Z: 4, Width: 5, Height: 5})
	stores := DeclareStores(StorageRequest{Incinerator: &room}).Stores
	var zone *Store
	for i, s := range stores {
		if s.Role == domain.IncineratorRole {
			zone = &stores[i]
		}
	}
	if zone == nil || zone.Interior != room.Interior || zone.Width != 0 || zone.Retired {
		t.Fatalf("incinerator store: %+v", zone)
	}
	if zone.Priority != domain.PreferredPriority || zone.Filter != domain.IncineratorFilter() {
		t.Fatal(zone.Priority, zone.Filter)
	}
	for _, s := range PlanStorage(StorageRequest{Bounds: Bounds{Width: 40, Height: 30}, Incinerator: &room}).Sites {
		if s.Role == domain.IncineratorRole {
			t.Fatal("the storage planner still sites the incinerator")
		}
	}
}

// The incinerator is a plan room, planned from the start inside the waste
// yard: it is never owed again once the plan holds one, and keeps its place.
func TestIncineratorRoomIsPlannedOnceAndInTheYard(t *testing.T) {
	room := incineratorRoom(Rectangle{X: 30, Z: 4, Width: 5, Height: 5})
	if room.Interior != (Rectangle{X: 31, Z: 5, Width: 3, Height: 3}) || room.Door != (domain.Cell{X: 30, Z: 6}) || room.DoorRot != domain.West || room.Outdoor {
		t.Fatal(room)
	}
	plan := LayoutPlan{Rooms: []PlannedRoom{room}}
	if missing := outskirtsMissing(plan, OutskirtsLayout{}); len(missing) == 0 {
		t.Fatal("the cluster's other rooms are still owed")
	}
	for _, m := range outskirtsMissing(plan, OutskirtsLayout{}) {
		if m.Role == PlannedIncinerator {
			t.Fatal("a second incinerator owed")
		}
	}
}

// The waste yard's one dump is a Sanitation store declared from the plan: the
// yard interior outside the incinerator's outline, Low priority, everything
// but the not-burnable special, and no dump role or siting remains.
func TestIncinerationOwnerDeclaresOneDumpOverTheYard(t *testing.T) {
	layout, ok := OutskirtsSlots(Rectangle{X: 10, Z: 10, Width: 40, Height: 30})
	if !ok {
		t.Fatal("no layout")
	}
	plan := LayoutPlan{Rooms: outskirtsRooms(layout)}
	view := StorageRequest{Layout: &plan}
	var dumps []Store
	for _, s := range DeclareStores(view).Stores {
		if s.Role == domain.DumpRole {
			dumps = append(dumps, s)
		}
	}
	if len(dumps) != 1 {
		t.Fatalf("%d dump stores, want one", len(dumps))
	}
	dump := dumps[0]
	if dump.Priority != domain.LowPriority || dump.Filter != domain.DumpFilter() || dump.Retired {
		t.Fatalf("dump store: %+v", dump)
	}
	if got, want := len(dump.footprint()), int(WasteYardW*WasteYardH-IncineratorOutline*IncineratorOutline); got != want {
		t.Fatalf("dump covers %d cells, want %d (yard less the incinerator outline)", got, want)
	}
	outline := cellSet(rectCells(layout.Incinerator))
	for _, c := range dump.footprint() {
		if outline[c] {
			t.Fatalf("dump cell %v inside the incinerator outline", c)
		}
	}
	if len((incinerationOwner{}).Stores(StorageRequest{})) != 0 {
		t.Fatal("a dump without a plan")
	}
}
