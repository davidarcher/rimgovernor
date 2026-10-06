package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The incinerator is zoned only after its walls stand: a priority above the
// Low dumps, taking what the rotten and worn dumps take.
func TestPlanStorageIncinerator(t *testing.T) {
	needs := map[string]int{domain.RottenDumpRole: 2}
	layout := LayoutPlan{Rooms: []PlannedRoom{incineratorRoom(Rectangle{X: 30, Z: 4, Width: 5, Height: 5})}}
	req := StorageRequest{Bounds: Bounds{Width: 40, Height: 30}, Cells: dumpCensus(40, 30, func(domain.Cell) bool { return false }), Layout: &layout, Rooms: &RoomObservation{},
		Dumps: &DumpStore{Needs: needs, Anchor: domain.Cell{X: 10, Z: 10}}}
	for _, s := range PlanStorage(req).Sites {
		if s.Role == domain.IncineratorRole {
			t.Fatal("zone before the walls stand")
		}
	}
	room := layout.IncineratorRooms()[0]
	req.Dumps.Incinerator = &room
	var zone *StockpileSite
	for _, s := range PlanStorage(req).Sites {
		if s.Role == domain.IncineratorRole {
			zone = &s
		}
	}
	if zone == nil || len(zone.Room) != 9 || len(zone.Candidates) != 1 || len(zone.Candidates[0]) != 9 {
		t.Fatalf("incinerator zone: %+v", zone)
	}
	if zone.Priority != domain.PreferredPriority {
		t.Fatal(zone.Priority)
	}
	// It takes what the rotten and worn dumps take, but not serviceable gear.
	allow := domain.IncineratorFilter().Allow()
	for _, want := range []domain.FilterSelector{domain.CategoryDef("Apparel"), domain.CategoryDef("Weapons"), domain.CategoryDef("CorpsesAnimal"), domain.CategoryDef("Foods")} {
		found := false
		for _, a := range allow {
			found = found || a == want
		}
		if !found {
			t.Errorf("filter lacks %v", want)
		}
	}
	if _, top, ok := domain.IncineratorFilter().HitPoints(); !ok || top != domain.GearHitPointFloor {
		t.Fatal("serviceable gear would be burned", top, ok)
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
