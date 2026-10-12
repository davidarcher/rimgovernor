package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func roleGroup(id int, role RoomRole, planned PlannedRole, area int) RoomGroup {
	return RoomGroup{ID: id, Role: role, Planned: planned, Area: area, Stations: []GroupStation{{Node: "Shelf", Def: "Shelf", Count: 1}}}
}

func TestOutgrownSuccessionCoversEachRole(t *testing.T) {
	link := domain.Cell{X: 1, Z: 1}
	plan := LayoutPlan{Rooms: []PlannedRoom{
		{ID: 1, Role: PlannedKitchen, Region: region(6, 5)},
		{ID: 2, Role: PlannedFreezer, Region: region(5, 5), Link: &link},
		{ID: 3, Role: PlannedButchery, Region: region(4, 4), Link: &link},
		{ID: 4, Role: PlannedDining, Region: region(9, 7), Link: &link},
		{ID: 5, Role: PlannedRec, Region: region(9, 7)},
	}, NextRoomID: 6}
	cases := []struct {
		name   string
		group  RoomGroup
		room   int32
		role   PlannedRole
		linked bool
	}{
		{"kitchen", roleGroup(0, RoomRoleKitchen, "", 50), 1, PlannedKitchen, false},
		{"freezer", roleGroup(0, RoomRoleStoreroom, PlannedFreezer, 40), 2, PlannedFreezer, true},
		{"butchery", roleGroup(0, RoomRoleKitchen, PlannedButchery, 30), 3, PlannedButchery, true},
		{"dining", roleGroup(0, RoomRoleDiningRoom, "", 80), 4, PlannedDining, true},
		{"rec", roleGroup(0, RoomRoleRecRoom, "", 80), 5, PlannedRec, false},
	}
	for _, c := range cases {
		area := plan.Rooms[c.room-1].Region.Area()
		got := OutgrownSuccession(outgrownAdvice(c.group, area, c.room), successionCatalog(), plan)
		if got.Replaces != c.room || got.Role != c.role || got.Linked != c.linked || got.Floor != area || got.Shortfall != c.group.Area-area {
			t.Errorf("%s: successor = %+v, want room %d (%s) linked %v", c.name, got, c.room, c.role, c.linked)
		}
		// Not outgrown: quiet. One more station short of fitting: quiet.
		if got := OutgrownSuccession(outgrownAdvice(c.group, c.group.Area), successionCatalog(), plan); got.Any() {
			t.Errorf("%s: asked for a successor with the floor met: %+v", c.name, got)
		}
		small := c.group
		small.Area = area + 2
		if got := OutgrownSuccession(outgrownAdvice(small, area, c.room), successionCatalog(), plan); got.Any() {
			t.Errorf("%s: asked for a successor 2 cells short of a shelf: %+v", c.name, got)
		}
	}
}

func TestMatchGroupRoomsKeepsTheFreezerAndButcheryApart(t *testing.T) {
	groups := []RoomGroup{
		roleGroup(0, RoomRoleKitchen, "", 40),
		roleGroup(1, RoomRoleKitchen, PlannedButchery, 12),
		roleGroup(2, RoomRoleStoreroom, "", 60),
		roleGroup(3, RoomRoleStoreroom, PlannedFreezer, 20),
	}
	plan := LayoutPlan{Rooms: []PlannedRoom{
		{ID: 1, Role: PlannedKitchen, Region: region(6, 5)},
		{ID: 2, Role: PlannedButchery, Region: region(4, 4)},
		{ID: 3, Role: PlannedStorage, Region: region(10, 5)},
		{ID: 4, Role: PlannedFreezer, Region: region(5, 5)},
	}}
	built := BuiltRegions(plan, map[int32]bool{1: true, 2: true, 3: true, 4: true})
	if len(built) != 4 {
		t.Fatalf("built regions = %+v, want all four standing rooms", built)
	}
	got := MatchGroupRooms(groups, built)
	for i, want := range []int32{1, 2, 3, 4} {
		if len(got[i].Rooms) != 1 || got[i].Rooms[0] != want {
			t.Errorf("group %d matched %v, want room %d", i, got[i].Rooms, want)
		}
	}
	if !GroupOutgrown(groups[0], got[0]) || !GroupOutgrown(groups[2], got[2]) || GroupOutgrown(groups[1], got[1]) || GroupOutgrown(groups[3], got[3]) {
		t.Errorf("outgrown = %v %v %v %v, want the kitchen and storage only", GroupOutgrown(groups[0], got[0]), GroupOutgrown(groups[1], got[1]), GroupOutgrown(groups[2], got[2]), GroupOutgrown(groups[3], got[3]))
	}
}

// A freezer successor is sited where its Link door opens on the kitchen: beside
// the kitchen that stays, never beside one a successor replaces.
func TestFreezerSuccessorKeepsItsLinkToTheKitchen(t *testing.T) {
	// The kitchen's sides hold the freezer being replaced and a hallway, so the
	// successor takes the kitchen's back wall.
	s := zoningSurvey(120, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan := corePlan(Zone(s), 4, TechTierCamp).WithRoomIDs()
	var old, kitchen PlannedRoom
	for _, r := range plan.Rooms {
		switch r.Role {
		case PlannedFreezer:
			old = r
		case PlannedKitchen:
			kitchen = r
		}
	}
	if old.Link == nil {
		t.Fatal("the plan's freezer has no Link to the kitchen")
	}
	all := allPins(plan)
	succ := Successor{Replaces: old.ID, Role: PlannedFreezer, Linked: true, Floor: old.Region.Area(), Shortfall: 6, Sizes: [][2]int32{{5, 5}}}
	next, changed, err := ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: all, Successor: succ}, 0, 3, 0, TechTierCamp, nil, nil)
	unsited, _ := SplitUnsited(err)
	if len(unsited) > 0 {
		t.Fatalf("not sited on open ground: %v", unsited[0])
	}
	if err != nil || !changed || !next.Valid() {
		t.Fatalf("changed %v, valid %v, err %v", changed, next.Valid(), err)
	}
	var added *PlannedRoom
	for _, r := range next.AllRooms() {
		if r.Replaces == old.ID {
			added = &r
		}
	}
	if added == nil || added.Role != PlannedFreezer || added.Link == nil {
		t.Fatalf("successor = %+v, want a freezer with a Link", added)
	}
	if !inWall(kitchen.Region.Bounds(), *added.Link) || !inWall(added.Region.Bounds(), *added.Link) {
		t.Fatalf("link %v is not a door between the kitchen %v and the freezer %v", *added.Link, kitchen.Region.Bounds(), added.Region.Bounds())
	}
}

func TestSuccessorOfALinkedRoomIsNotSitedWithoutALink(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	plan, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 0, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	var old PlannedRoom
	for _, r := range plan.Rooms {
		if r.Role == PlannedKitchen {
			old = r
		}
	}
	all := allPins(plan)
	// A kitchen has no neighbour rule, so no slot has a Link: refused, not built.
	// (No census, so the room is not reshaped in place instead.)
	succ := Successor{Replaces: old.ID, Role: PlannedKitchen, Linked: true, Floor: old.Region.Area(), Shortfall: 6, Sizes: [][2]int32{{7, 5}}}
	next, _, err := ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: all, Successor: succ}, 0, 3, 0, TechTierCamp, nil, nil)
	if unsited, _ := SplitUnsited(err); len(unsited) != 1 {
		t.Fatalf("unsited = %v, err %v", unsited, err)
	}
	for _, r := range next.AllRooms() {
		if r.Replaces != 0 {
			t.Fatalf("built %+v for a successor that kept no link", r)
		}
	}
}

func TestOutgrownBedroomRetiresItsWingOnce(t *testing.T) {
	wing := func(purpose WingPurpose, ids ...int32) Wing {
		w := Wing{Purpose: purpose}
		for _, id := range ids {
			w.Rooms = append(w.Rooms, PlannedRoom{ID: id, Role: PlannedBedroom, Region: region(3, 4)})
		}
		return w
	}
	group := roleGroup(0, RoomRoleBedroom, "", 18)
	plan := LayoutPlan{Wings: []Wing{wing(WingBedrooms, 1, 2)}, NextRoomID: 3}
	got := OutgrownSuccession(outgrownAdvice(group, 12, 1, 2), successionCatalog(), plan)
	if got.Replaces != 1 || got.Role != PlannedBedroom || got.Floor != 12 || got.Shortfall != 6 {
		t.Fatalf("successor = %+v, want bedroom 1 at 12 + 6", got)
	}
	// A wing still emptying: no second rebuild.
	plan.Wings = []Wing{wing(WingBedroomsRetiring, 1, 2), wing(WingBedrooms, 3)}
	if got := OutgrownSuccession(outgrownAdvice(group, 12, 3), successionCatalog(), plan); got.Any() {
		t.Fatalf("asked for a bedroom successor while a wing retires: %+v", got)
	}
	// A bedroom that is not in a wing is not replaced by this path.
	plan = LayoutPlan{Rooms: []PlannedRoom{{ID: 1, Role: PlannedBedroom, Region: region(3, 4)}}, NextRoomID: 2}
	if got := OutgrownSuccession(outgrownAdvice(group, 12, 1), successionCatalog(), plan); got.Any() {
		t.Fatalf("replaced a bedroom outside the wings: %+v", got)
	}
}

func TestBedroomWingSuccessorMigratesSleepersWithoutAnUnbeddedNight(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	plan, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 0, 0).Value()
	if !ok || len(bedroomWings(plan.Wings)) == 0 {
		t.Fatal("no plan with a bedroom wing")
	}
	oldWing := plan.Wings[bedroomWings(plan.Wings)[0]]
	first := oldWing.Rooms[0]
	all := allPins(plan)
	succ := Successor{Replaces: first.ID, Role: PlannedBedroom, Floor: first.Region.Area(), Shortfall: 8, Sizes: [][2]int32{{5, 4}, {4, 5}}}
	if got := succ.WingSize(TechTierCamp); got != [2]int32{5, 4} {
		t.Fatalf("wing size = %v, want the block's 5x4", got)
	}
	next, changed, err := ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: all, Successor: succ}, 0, 3, 0, TechTierCamp, nil, nil)
	if err != nil || !changed || !next.Valid() {
		t.Fatalf("changed %v, valid %v, err %v", changed, next.Valid(), err)
	}
	var retiring, active []Wing
	for _, w := range next.Wings {
		switch w.Purpose {
		case WingBedroomsRetiring:
			retiring = append(retiring, w)
		case WingBedrooms:
			active = append(active, w)
		}
	}
	if len(retiring) != 1 || len(retiring[0].Rooms) != len(oldWing.Rooms) || retiring[0].Rooms[0].ID != first.ID {
		t.Fatalf("retiring wings = %+v, want the old wing as it was", retiring)
	}
	if len(active) == 0 {
		t.Fatal("no new wing sited")
	}
	for _, r := range active[0].Rooms {
		if r.Region.Area() != 20 {
			t.Fatalf("new wing room %v, want 5x4", r.Region.Bounds())
		}
	}
	// The sleepers: pawn a owns the old wing's first bed, the new wing's first
	// room stands with a vacant bed. a is moved straight to it, and the old wing
	// keeps every bed until then.
	standing := func(r PlannedRoom, id string, beds ...string) Room {
		return Room{ID: id, Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: beds, Cells: []domain.Cell{r.Region.anchor()}}
	}
	bed := func(id string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: owners, AccessibleTo: []PawnID{"a"}}
	}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{standing(retiring[0].Rooms[0], "old", "ob"), standing(active[0].Rooms[0], "new", "nb")}}
	sleeping := SleepingObservation{Colonists: 1,
		People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("ob")}},
		Beds:   []SleepingBed{bed("ob", "a"), bed("nb")}}
	if got := EmptiedRetiringWings(next, domain.Known(rooms), domain.Known(sleeping)); len(got) != 0 {
		t.Fatalf("the old wing is emptied while a sleeps in it: %v", got)
	}
	if pins := replanPins(next, map[int32]bool{first.ID: true}, nil, TechTierCamp); !pins[first.ID] {
		t.Fatal("the old wing was unpinned while a sleeps in it")
	}
	step := NextMigrateStep(next, rooms, sleeping)
	if step.Kind != BedroomMove || step.Pawn != "a" || step.Bed != "nb" || step.PreviousBed != "ob" {
		t.Fatalf("step = %+v, want a moved straight from ob to nb", step)
	}
	sleeping.People[0].OwnedBed = domain.Known("nb")
	sleeping.Beds[0].Owners, sleeping.Beds[1].Owners = nil, []PawnID{"a"}
	emptied := EmptiedRetiringWings(next, domain.Known(rooms), domain.Known(sleeping))
	if !emptied[retiring[0].Corridor.From] {
		t.Fatalf("emptied = %v, want the old wing once a has moved", emptied)
	}
	if pins := replanPins(next, map[int32]bool{first.ID: true}, emptied, TechTierCamp); pins[first.ID] {
		t.Fatal("the emptied wing stayed pinned")
	}
}
