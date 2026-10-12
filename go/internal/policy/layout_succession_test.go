package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func successionCatalog() StationCatalog {
	return StationCatalog{Stations: map[string]Station{
		"Shelf": {Def: "Shelf", Shape: InteriorPieceDef{Def: "Shelf", Size: domain.Cell{X: 1, Z: 2}}},
		"Bench": {Def: "Bench", Shape: InteriorPieceDef{Def: "Bench", Size: domain.Cell{X: 3, Z: 1}}},
	}}
}

func storageGroup(id, area int, defs ...string) RoomGroup {
	g := RoomGroup{ID: id, Role: RoomRoleStoreroom, Area: area}
	for _, d := range defs {
		g.Stations = append(g.Stations, GroupStation{Node: d, Def: d, Count: 1})
	}
	return g
}

func outgrownAdvice(g RoomGroup, capacity int, rooms ...int32) StationAdvice {
	m := GroupRooms{Group: g.ID, Rooms: rooms, Capacity: capacity}
	return StationAdvice{
		Groups:    []GroupAdvice{{Group: g, Rooms: rooms, Capacity: capacity, Outgrown: GroupOutgrown(g, m)}},
		Embedding: Embedding{Blocks: []EmbeddedBlock{{Group: g.ID, Interior: Rectangle{Width: 10, Height: 8}}}},
	}
}

func successionPlan() LayoutPlan {
	return LayoutPlan{Rooms: []PlannedRoom{
		{ID: 1, Role: PlannedStorage, Region: region(5, 4)},  // 20
		{ID: 2, Role: PlannedStorage, Region: region(6, 6)},  // 36
		{ID: 3, Role: PlannedArmory, Region: region(3, 3)},   // 9, another role of the storeroom
		{ID: 4, Role: PlannedWorkshop, Region: region(7, 5)}, // 35
	}, NextRoomID: 5}
}

func TestOutgrownSuccessionReplacesTheSmallestMatchedRoomAtItsFloorPlusShortfall(t *testing.T) {
	plan := successionPlan()
	// The group needs 80; rooms 1, 2 and the armory 3 hold 65: 15 short. The
	// armory is the smallest matched room but belongs to #2864, so room 1 goes.
	advice := outgrownAdvice(storageGroup(0, 80, "Shelf"), 65, 1, 2, 3)
	got := OutgrownSuccession(advice, successionCatalog(), plan)
	if got.Replaces != 1 || got.Role != PlannedStorage || got.Floor != 20 || got.Shortfall != 15 {
		t.Fatalf("successor = %+v, want room 1 at floor 20 + 15", got)
	}
	// 35 cells from the 10x8 block: its depth is cut to 4 (10x4 = 40), and its turn.
	if want := [][2]int32{{10, 4}, {4, 10}}; !reflect.DeepEqual(got.Sizes, want) {
		t.Fatalf("sizes = %v, want %v", got.Sizes, want)
	}
}

func TestOutgrownSuccessionWaitsForOneMoreStation(t *testing.T) {
	plan := successionPlan()
	catalog := successionCatalog()
	// A shelf is 2 cells and one open cell beside it: 3. Two short is not outgrown.
	if got := OutgrownSuccession(outgrownAdvice(storageGroup(0, 67, "Shelf"), 65, 1, 2), catalog, plan); got.Any() {
		t.Fatalf("a shortfall under the smallest station asked for %+v", got)
	}
	if got := OutgrownSuccession(outgrownAdvice(storageGroup(0, 68, "Shelf"), 65, 1, 2), catalog, plan); !got.Any() {
		t.Fatal("a shortfall holding the smallest station asked for nothing")
	}
	// The smallest station of the group sets the bar: a bench (4) beside a shelf (3).
	if got := OutgrownSuccession(outgrownAdvice(storageGroup(0, 68, "Bench", "Shelf"), 65, 1, 2), catalog, plan); !got.Any() {
		t.Fatal("the shelf is the smallest station and fits")
	}
	if got := OutgrownSuccession(outgrownAdvice(storageGroup(0, 68, "Bench"), 65, 1, 2), catalog, plan); got.Any() {
		t.Fatal("a bench does not fit in 3 cells")
	}
	// A group with no readable station shape is left as it is, never guessed.
	if got := OutgrownSuccession(outgrownAdvice(storageGroup(0, 90, "Unknown"), 65, 1, 2), catalog, plan); got.Any() {
		t.Fatal("an unreadable station shape asked for a successor")
	}
}

func TestOutgrownSuccessionIsQuietWhenNotOutgrownOrAnotherRole(t *testing.T) {
	plan := successionPlan()
	catalog := successionCatalog()
	if got := OutgrownSuccession(outgrownAdvice(storageGroup(0, 60, "Shelf"), 65, 1, 2), catalog, plan); got.Any() {
		t.Fatalf("a housed group asked for %+v", got)
	}
	if got := OutgrownSuccession(outgrownAdvice(storageGroup(0, 80, "Shelf"), 9, 3), catalog, plan); got.Any() {
		t.Fatalf("an armory is #2864's, got %+v", got)
	}
	work := RoomGroup{ID: 1, Role: RoomRoleWorkshop, Area: 60, Stations: []GroupStation{{Node: "Bench", Def: "Bench", Count: 1}}}
	got := OutgrownSuccession(outgrownAdvice(work, 35, 4), catalog, plan)
	if got.Replaces != 4 || got.Role != PlannedWorkshop || got.Shortfall != 25 {
		t.Fatalf("workshop successor = %+v", got)
	}
}

func TestOutgrownSuccessionHasOneRebuildInFlight(t *testing.T) {
	plan := successionPlan()
	advice := outgrownAdvice(storageGroup(0, 200, "Shelf"), 56, 1, 2)
	if !OutgrownSuccession(advice, successionCatalog(), plan).Any() {
		t.Fatal("no successor asked for")
	}
	plan.Rooms = append(plan.Rooms, PlannedRoom{ID: 5, Role: PlannedStorage, Replaces: 1, Region: region(12, 7)})
	plan.NextRoomID = 6
	if !SuccessorPending(plan) {
		t.Fatal("an unretired predecessor is not pending")
	}
	if got := OutgrownSuccession(advice, successionCatalog(), plan); got.Any() {
		t.Fatalf("a second rebuild asked for while one is in flight: %+v", got)
	}
}

func TestRoomChainsCountAReplacesChainAsOneRoom(t *testing.T) {
	plan := successionPlan()
	if plan.RoomChains(PlannedStorage) != 2 {
		t.Fatal("two storage rooms")
	}
	plan.Rooms = append(plan.Rooms, PlannedRoom{ID: 5, Role: PlannedStorage, Replaces: 1, Region: region(12, 7)})
	if got := plan.RoomChains(PlannedStorage); got != 2 {
		t.Fatalf("a room and its successor count %d, want 2 with room 2", got)
	}
	// Owed counts do not re-add a twin while the pair stands.
	if got := StorageRoomsOwed(plan, RoomDemand{Storage: 2}); got != 0 {
		t.Fatalf("owed %d, want none", got)
	}
	if got := StorageRoomsOwed(plan, RoomDemand{Storage: 3}); got != 1 {
		t.Fatalf("owed %d, want 1", got)
	}
	// The pair is one room standing, too: the predecessor is not matched twice.
	got := BuiltRegions(plan, map[int32]bool{1: true, 2: true, 5: true})
	if len(got) != 2 || got[0].Room != 2 || got[1].Room != 5 {
		t.Fatalf("built regions = %+v, want the successor and room 2", got)
	}
}

func TestRetireSurplusDropsThePredecessorOnceTheSuccessorStands(t *testing.T) {
	plan := successionPlan()
	plan.Rooms = append(plan.Rooms, PlannedRoom{ID: 5, Role: PlannedStorage, Replaces: 1, Region: region(12, 7)})
	if _, changed := retireSurplusRooms(plan, RoomGrowth{}, map[int32]bool{1: true}); changed {
		t.Fatal("the predecessor left before its successor stood")
	}
	got, changed := retireSurplusRooms(plan, RoomGrowth{}, map[int32]bool{1: true, 5: true})
	if !changed || got.RoomChains(PlannedStorage) != 2 || len(got.roomsOf(PlannedStorage)) != 2 {
		t.Fatalf("retired = %v, rooms %d", changed, len(got.roomsOf(PlannedStorage)))
	}
	for _, r := range got.AllRooms() {
		if r.ID == 1 {
			t.Fatal("the built predecessor stayed")
		}
	}
	// An idle storage reading does not drop the successor in flight.
	idle, _ := retireSurplusRooms(plan, RoomGrowth{Demand: RoomDemand{StorageIdle: true}}, map[int32]bool{1: true})
	if got := idle.roomsOf(PlannedStorage); len(got) != 2 || got[0].ID != 1 || got[1].ID != 5 {
		t.Fatalf("idle reading kept %v, want the pair in flight only", idle.roomsOf(PlannedStorage))
	}
}

func TestReplanPinsKeepAPairInFlight(t *testing.T) {
	plan := successionPlan()
	plan.Rooms = append(plan.Rooms, PlannedRoom{ID: 5, Role: PlannedStorage, Replaces: 1, Region: region(12, 7)})
	pins := replanPins(plan, map[int32]bool{}, nil, TechTierCamp)
	if !pins[1] || !pins[5] || pins[2] {
		t.Fatalf("pins = %v, want the pair only", pins)
	}
}

func TestReplanSitesTheSuccessorThenRetiresThePredecessor(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	plan, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	var old PlannedRoom
	for _, r := range plan.Rooms {
		if r.Role == PlannedStorage {
			old = r
		}
	}
	all := allPins(plan)
	succ := Successor{Replaces: old.ID, Role: PlannedStorage, Floor: old.Region.Area(), Shortfall: 30, Sizes: [][2]int32{{16, 9}, {9, 16}}}

	next, changed, err := ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: all, Built: all, Successor: succ}, 0, 3, 0, TechTierCamp, nil, nil)
	if err != nil || !changed || !next.Valid() {
		t.Fatalf("changed %v, valid %v, err %v", changed, next.Valid(), err)
	}
	var added *PlannedRoom
	for _, r := range next.AllRooms() {
		if r.Replaces == old.ID {
			added = &r
		}
	}
	if added == nil || added.Role != PlannedStorage || added.Region.Area() < succ.Floor+succ.Shortfall || added.ID == 0 {
		t.Fatalf("successor = %+v", added)
	}
	if planned, retired := SuccessionChanges(plan, next); len(planned) != 1 || len(retired) != 0 {
		t.Fatalf("changes = %v, %v", planned, retired)
	}
	if got := next.roomsOf(PlannedStorage); len(got) != 2 {
		t.Fatalf("storage rooms %d, want the pair", len(got))
	}
	// The pair stays, the old room first, until the successor stands.
	built := allPins(next)
	again, _, _ := ReplanLayoutWithRooms(next, s, RoomGrowth{Fixed: built, Built: built}, 0, 3, 0, TechTierCamp, nil, nil)
	for _, r := range again.AllRooms() {
		if r.ID == old.ID {
			t.Fatal("the predecessor stayed with its successor standing")
		}
	}
	if planned, retired := SuccessionChanges(next, again); len(planned) != 0 || len(retired) != 1 || retired[0].ID != old.ID {
		t.Fatalf("changes = %v, %v", planned, retired)
	}
	// No churn: an unchanged colony asks for no more.
	if settled, changed, _ := ReplanLayoutWithRooms(again, s, RoomGrowth{Fixed: allPins(again), Built: allPins(again)}, 0, 3, 0, TechTierCamp, nil, nil); changed && !reflect.DeepEqual(settled, again) {
		if p, r := SuccessionChanges(again, settled); len(p)+len(r) > 0 {
			t.Fatalf("a settled plan changed its rooms: %v %v", p, r)
		}
	}
}

func TestReplanReportsASuccessorItCannotSite(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	plan, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	var old PlannedRoom
	for _, r := range plan.Rooms {
		if r.Role == PlannedStorage {
			old = r
		}
	}
	all := allPins(plan)
	for name, sizes := range map[string][][2]int32{"no block": nil, "too large": {{900, 900}}} {
		succ := Successor{Replaces: old.ID, Role: PlannedStorage, Floor: old.Region.Area(), Shortfall: 30, Sizes: sizes}
		next, _, err := ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: all, Built: all, Successor: succ}, 0, 3, 0, TechTierCamp, nil, nil)
		unsited, _ := SplitUnsited(err)
		if len(unsited) != 1 || unsited[0].Successor.Replaces != old.ID || unsited[0].Reason == "" {
			t.Fatalf("%s: unsited = %v, err %v", name, unsited, err)
		}
		for _, r := range next.AllRooms() {
			if r.Replaces != 0 {
				t.Fatalf("%s: built %+v for a successor that could not be sited", name, r)
			}
		}
	}
}
