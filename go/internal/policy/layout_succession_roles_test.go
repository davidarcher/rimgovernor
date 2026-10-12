package policy

import (
	"slices"
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
	plan, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 0).Value()
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

func TestOutgrownBedroomIsAnOrdinarySuccessorOnce(t *testing.T) {
	group := roleGroup(0, RoomRoleBedroom, "", 18)
	plan := LayoutPlan{
		Rooms:      []PlannedRoom{{ID: 1, Role: PlannedBedroom, Region: region(3, 4)}},
		Wings:      []Wing{{Purpose: WingBedrooms, Rooms: []PlannedRoom{{ID: 2, Role: PlannedBedroom, Region: region(3, 3)}}}},
		NextRoomID: 3,
	}
	got := OutgrownSuccession(outgrownAdvice(group, 12, 1, 2), successionCatalog(), plan)
	if got.Replaces != 2 || got.Role != PlannedBedroom || got.Floor != 9 || got.Shortfall != 6 {
		t.Fatalf("successor = %+v, want the smallest bedroom, 2, at 9 + 6", got)
	}
	// One rebuild at a time: a bedroom already replaced asks for no second.
	plan.Rooms = append(plan.Rooms, PlannedRoom{ID: 3, Replaces: 2, Role: PlannedBedroom, Region: region(5, 4)})
	plan.NextRoomID = 4
	if got := OutgrownSuccession(outgrownAdvice(group, 12, 1, 3), successionCatalog(), plan); got.Any() {
		t.Fatalf("asked for a second rebuild while one is in flight: %+v", got)
	}
}

// A replaced bedroom keeps its sleepers until each has moved straight to a
// vacant bed of the successor, and leaves the plan only then.
func TestBedroomSuccessorMigratesSleepersWithoutAnUnbeddedNight(t *testing.T) {
	at := func(id, replaces int32, x int32) PlannedRoom {
		return PlannedRoom{ID: id, Replaces: replaces, Role: PlannedBedroom, Region: RectRegion(Rectangle{X: x, Z: 0, Width: 3, Height: 4})}
	}
	old, next := at(1, 0, 0), at(2, 1, 10)
	plan := LayoutPlan{Rooms: []PlannedRoom{old, next}, NextRoomID: 3}
	standing := func(r PlannedRoom, id string, beds ...string) Room {
		return Room{ID: id, Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: beds, Cells: []domain.Cell{r.Region.anchor()}}
	}
	bed := func(id string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: owners, AccessibleTo: []PawnID{"a"}}
	}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{standing(old, "old", "ob"), standing(next, "new", "nb")}}
	sleeping := SleepingObservation{Colonists: 1,
		People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("ob")}},
		Beds:   []SleepingBed{bed("ob", "a"), bed("nb")}}
	slept := SleptBedrooms(plan, domain.Known(rooms), domain.Known(sleeping))
	if !slept[old.ID] || slept[next.ID] {
		t.Fatalf("slept = %v, want the old bedroom while a sleeps in it", slept)
	}
	// The successor stands, but the old room stays while it is slept in.
	growth := RoomGrowth{Slept: slept, Built: map[int32]bool{1: true, 2: true}}
	if kept, dropped := retireSurplusRooms(plan, growth, growth.Built); dropped || len(kept.AllRooms()) != 2 {
		t.Fatalf("the old bedroom left the plan while a sleeps in it: %+v", kept.AllRooms())
	}
	if pins := replanPins(plan, map[int32]bool{next.ID: true}, nil, TechTierCamp); !pins[old.ID] {
		t.Fatal("the old bedroom was unpinned while a sleeps in it")
	}
	step := NextMigrateStep(plan, rooms, sleeping)
	if step.Kind != BedroomMove || step.Pawn != "a" || step.Bed != "nb" || step.PreviousBed != "ob" {
		t.Fatalf("step = %+v, want a moved straight from ob to nb", step)
	}
	sleeping.People[0].OwnedBed = domain.Known("nb")
	sleeping.Beds[0].Owners, sleeping.Beds[1].Owners = nil, []PawnID{"a"}
	if slept := SleptBedrooms(plan, domain.Known(rooms), domain.Known(sleeping)); len(slept) != 0 {
		t.Fatalf("slept = %v after a moved", slept)
	}
	growth.Slept = nil
	kept, dropped := retireSurplusRooms(plan, growth, growth.Built)
	if !dropped || len(kept.AllRooms()) != 1 || kept.AllRooms()[0].ID != next.ID {
		t.Fatalf("rooms = %+v, want the old bedroom gone once a has moved", kept.AllRooms())
	}
	// An unread census keeps every replaced bedroom.
	if slept := SleptBedrooms(plan, domain.Fact[RoomObservation]{}, domain.Known(sleeping)); !slept[old.ID] {
		t.Fatalf("slept = %v with the census unknown", slept)
	}
}

// opensOnGround are the rooms of plan whose Link is no cell of another room's
// walls: a door onto open ground.
func opensOnGround(plan LayoutPlan) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range plan.AllRooms() {
		if r.Link == nil {
			continue
		}
		onto := false
		for _, o := range plan.AllRooms() {
			onto = onto || o.ID != r.ID && inWall(o.Region.Bounds(), *r.Link) && inWall(r.Region.Bounds(), *r.Link)
		}
		if !onto {
			out = append(out, r)
		}
	}
	return out
}

// linkedRooms are a kitchen, the freezer beside it, the dining room beside the
// freezer and the butchery behind it, each Link in the wall it shares.
func linkedRooms() (kitchen, freezer, dining, butchery PlannedRoom) {
	in := func(x, z, w, h int32) Region { return RectRegion(Rectangle{X: x, Z: z, Width: w, Height: h}) }
	link := func(x, z int32) *domain.Cell { return &domain.Cell{X: x, Z: z} }
	kitchen = PlannedRoom{ID: 1, Role: PlannedKitchen, Region: in(0, 0, 5, 5)}
	freezer = PlannedRoom{ID: 2, Role: PlannedFreezer, Region: in(6, 0, 4, 5), Link: link(5, 2)}
	dining = PlannedRoom{ID: 3, Role: PlannedDining, Region: in(11, 0, 4, 5), Link: link(10, 2)}
	butchery = PlannedRoom{ID: 4, Role: PlannedButchery, Region: in(6, 6, 4, 3), Link: link(7, 5)}
	return
}

// The dining room and butchery opened into the freezer: once its successor
// stands and it leaves, their doors are in the successor's walls, none onto the
// ground clearance takes.
func TestFreezerSuccessorRepointsTheNeighboursLinks(t *testing.T) {
	kitchen, freezer, dining, butchery := linkedRooms()
	succ := PlannedRoom{ID: 5, Replaces: 2, Role: PlannedFreezer, Region: RectRegion(Rectangle{X: 11, Z: 6, Width: 4, Height: 3})}
	plan := LayoutPlan{Rooms: []PlannedRoom{kitchen, freezer, dining, butchery, succ}, NextRoomID: 6}
	growth := RoomGrowth{Built: map[int32]bool{1: true, 2: true, 3: true, 4: true, 5: true}}
	next, dropped := retireSurplusRooms(plan, growth, growth.Built)
	if !dropped || len(next.AllRooms()) != 4 {
		t.Fatalf("rooms = %+v, want the old freezer gone", next.AllRooms())
	}
	for _, r := range next.Rooms {
		if (r.ID == dining.ID || r.ID == butchery.ID) && (r.Link == nil || !inWall(succ.Region.Bounds(), *r.Link)) {
			t.Errorf("%s link = %v, want a door in the successor's wall", r.Role, r.Link)
		}
	}
	if got := opensOnGround(next); len(got) != 0 {
		t.Fatalf("doors open onto cleared ground: %+v", got)
	}
}

// The freezer's Link into a replaced kitchen moves to the kitchen that stands,
// and a neighbour that shares no wall with its successor loses its Link rather
// than keep a door onto cleared ground.
func TestKitchenSuccessorRepointsTheFreezersLink(t *testing.T) {
	kitchen, freezer, dining, butchery := linkedRooms()
	succ := PlannedRoom{ID: 5, Replaces: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 0, Z: 6, Width: 9, Height: 3})}
	plan := LayoutPlan{Rooms: []PlannedRoom{kitchen, freezer, dining, butchery, succ}, NextRoomID: 6}
	growth := RoomGrowth{Built: map[int32]bool{1: true, 2: true, 3: true, 4: true, 5: true}}
	next, dropped := retireSurplusRooms(plan, growth, growth.Built)
	if !dropped {
		t.Fatal("the old kitchen stayed")
	}
	for _, r := range next.Rooms {
		switch r.ID {
		case freezer.ID:
			if r.Link == nil || !inWall(succ.Region.Bounds(), *r.Link) || !inWall(freezer.Region.Bounds(), *r.Link) {
				t.Errorf("freezer link = %v, want a door in the new kitchen's wall", r.Link)
			}
		case dining.ID:
			if r.Link == nil || *r.Link != *dining.Link {
				t.Errorf("dining link = %v, want it untouched", r.Link)
			}
		}
	}
	if got := opensOnGround(next); len(got) != 0 {
		t.Fatalf("doors open onto cleared ground: %+v", got)
	}
	plan.Rooms[4] = PlannedRoom{ID: 5, Replaces: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: -20, Z: 0, Width: 5, Height: 5})}
	next, _ = retireSurplusRooms(plan, growth, growth.Built)
	for _, r := range next.Rooms {
		if r.ID == freezer.ID && r.Link != nil {
			t.Errorf("freezer link = %v, want none with no kitchen beside it", *r.Link)
		}
	}
}

// A tiled plan keeps its doors in LayoutPlan.Links. The freezer's successor
// moves them: the dining room's door into the successor's wall, and the
// butchery, which shares no wall with the successor, loses its entry. A built
// door left without a link is taken out by reconcile, not stranded.
func TestFreezerSuccessorRepointsTiledLinks(t *testing.T) {
	kitchen, freezer, dining, butchery := linkedRooms()
	for _, r := range []*PlannedRoom{&freezer, &dining, &butchery} {
		r.Link = nil
	}
	succ := PlannedRoom{ID: 5, Replaces: 2, Role: PlannedFreezer, Region: RectRegion(Rectangle{X: 16, Z: 0, Width: 4, Height: 5})}
	plan := LayoutPlan{Rooms: []PlannedRoom{kitchen, freezer, dining, butchery, succ}, NextRoomID: 6, Links: []RoomLink{
		{A: 1, B: 2, Cell: domain.Cell{X: 5, Z: 2}}, {A: 2, B: 3, Cell: domain.Cell{X: 10, Z: 2}}, {A: 2, B: 4, Cell: domain.Cell{X: 7, Z: 5}},
	}}
	built := ringWalls(plan, butchery)
	growth := RoomGrowth{Built: map[int32]bool{1: true, 2: true, 3: true, 4: true, 5: true}}
	next, dropped := retireSurplusRooms(plan, growth, growth.Built)
	if !dropped {
		t.Fatal("the old freezer stayed")
	}
	for _, l := range next.Links {
		if l.A == 2 || l.B == 2 {
			t.Errorf("link %+v still names the dropped freezer", l)
		}
	}
	if got := next.LinksOf(dining); len(got) != 1 || !inWall(succ.Region.Bounds(), got[0]) || !inWall(dining.Region.Bounds(), got[0]) {
		t.Errorf("dining links = %v, want one door in the successor's wall", got)
	}
	if got := next.LinksOf(butchery); len(got) != 0 {
		t.Fatalf("butchery links = %v, want none: it shares no wall with the successor", got)
	}
	// The butchery's built door onto the cleared freezer comes out.
	in := ReconcileInput{Plan: next, Room: butchery, Ground: built, Rooms: RoomObservation{Shapes: testShapes}, WantedFloor: func(domain.Cell) string { return "" }}
	var out []domain.Cell
	for _, op := range Reconcile(in).Ready {
		if op.Kind == OpDoorOut {
			out = append(out, op.Cells...)
		}
	}
	if !slices.Equal(out, []domain.Cell{{X: 7, Z: 5}}) {
		t.Fatalf("door_out cells = %v, want the stale door at 7,5", out)
	}
}
