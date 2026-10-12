package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rect(x, z, w, h int32) Region { return RectRegion(Rectangle{X: x, Z: z, Width: w, Height: h}) }

// reshapePlan is one storeroom, 6x4 at (10, 10), door on its south wall, in
// open core ground, with a second room to its east.
func reshapePlan() (LayoutPlan, map[domain.Cell]bool) {
	plan := LayoutPlan{
		Rooms: []PlannedRoom{
			{ID: 1, Role: PlannedStorage, Region: rect(10, 10, 6, 4), Door: domain.Cell{X: 12, Z: 14}, DoorRot: domain.South},
			{ID: 2, Role: PlannedWorkshop, Region: rect(30, 10, 5, 5), Door: domain.Cell{X: 32, Z: 15}, DoorRot: domain.South},
		},
		Zones:      []LayoutZone{{Kind: ZoneCore}},
		NextRoomID: 3,
	}
	for z := int32(0); z < 40; z++ {
		plan.Zones[0].Runs = append(plan.Zones[0].Runs, RowRun{Z: z, X: 0, Length: 50})
	}
	return plan, nil
}

func TestDiffRegionsEnlargeShrinkAndReshape(t *testing.T) {
	old := rect(10, 10, 6, 4)

	grown := DiffRegions(old, rect(10, 10, 8, 4)) // two columns east
	if grown.Added.Area() != 8 || !grown.Removed.Empty() {
		t.Fatalf("enlarge: added %d removed %d", grown.Added.Area(), grown.Removed.Area())
	}
	// The east wall column (x=16, 4 cells plus the corners' column) moves to x=18.
	if !grown.WallsRemoved.Contains(domain.Cell{X: 16, Z: 11}) || !grown.WallsAdded.Contains(domain.Cell{X: 18, Z: 11}) {
		t.Fatalf("enlarge walls: +%v -%v", grown.WallsAdded.Cells(), grown.WallsRemoved.Cells())
	}

	shrunk := DiffRegions(old, rect(10, 10, 6, 3)) // the south row goes
	if shrunk.Removed.Area() != 6 || !shrunk.Added.Empty() {
		t.Fatalf("shrink: added %d removed %d", shrunk.Added.Area(), shrunk.Removed.Area())
	}
	if !shrunk.WallsRemoved.Contains(domain.Cell{X: 12, Z: 14}) || !shrunk.WallsAdded.Contains(domain.Cell{X: 12, Z: 13}) {
		t.Fatalf("shrink walls: +%v -%v", shrunk.WallsAdded.Cells(), shrunk.WallsRemoved.Cells())
	}

	// Moving the west wall one cell east: a column lost on the west, one gained on the east.
	moved := DiffRegions(old, rect(11, 10, 6, 4))
	if moved.Added.Area() != 4 || moved.Removed.Area() != 4 || !moved.Added.Contains(domain.Cell{X: 16, Z: 10}) || !moved.Removed.Contains(domain.Cell{X: 10, Z: 10}) {
		t.Fatalf("move: +%v -%v", moved.Added.Cells(), moved.Removed.Cells())
	}

	if !DiffRegions(old, old).Empty() {
		t.Fatal("the same region has a diff")
	}
}

func TestReshapeRoomEnlargesInPlaceAndRetiresTheOldWalls(t *testing.T) {
	plan, blocked := reshapePlan()
	next, diff, ok := ReshapeRoom(plan, 1, rect(10, 10, 8, 4), blocked)
	if !ok || diff.Added.Area() != 8 {
		t.Fatalf("ok %v, diff %+v", ok, diff)
	}
	r := next.Rooms[0]
	if r.ID != 1 || r.Role != PlannedStorage || !r.Region.Equal(rect(10, 10, 8, 4)) || r.Door != plan.Rooms[0].Door || r.Replaces != 0 {
		t.Fatalf("room = %+v", r)
	}
	if !plan.Rooms[0].Region.Equal(rect(10, 10, 6, 4)) {
		t.Fatal("the input plan changed")
	}
	// The old east wall now stands inside the room: it is ground to clear. The
	// floor both shapes share and the new walls are not.
	ground := RegionOfRuns(nil)
	for _, g := range next.RetiredGround {
		ground = unionRegions(ground, g)
	}
	if !ground.Contains(domain.Cell{X: 16, Z: 11}) || ground.Contains(domain.Cell{X: 12, Z: 11}) || ground.Contains(domain.Cell{X: 18, Z: 11}) {
		t.Fatalf("retired ground = %v", ground.Cells())
	}
	// The retired ground's old wall is not held by the room's own ring.
	if rg := RetiredGroundOf(next); rg.kept(domain.Cell{X: 16, Z: 11}) || !rg.kept(domain.Cell{X: 18, Z: 11}) {
		t.Fatal("the old wall inside the room reads as the room's wall")
	}
	if got := ReshapedRooms(plan, next); len(got) != 1 || got[0].Room.ID != 1 || got[0].Diff.Added.Area() != 8 {
		t.Fatalf("reshaped = %+v", got)
	}
}

func TestReshapeRoomShrinksWithTheSameOperation(t *testing.T) {
	plan, blocked := reshapePlan()
	next, diff, ok := ReshapeRoom(plan, 1, rect(10, 11, 6, 3), blocked)
	if !ok || diff.Removed.Area() != 6 {
		t.Fatalf("ok %v, diff %+v", ok, diff)
	}
	ground := RegionOfRuns(nil)
	for _, g := range next.RetiredGround {
		ground = unionRegions(ground, g)
	}
	// The old north wall row is ground to clear; the new north wall is the old first floor row.
	if !ground.Contains(domain.Cell{X: 11, Z: 9}) || ground.Contains(domain.Cell{X: 11, Z: 10}) || ground.Contains(domain.Cell{X: 11, Z: 12}) {
		t.Fatalf("retired ground = %v", ground.Cells())
	}
}

func TestReshapeRoomRefusesWhatDoesNotFit(t *testing.T) {
	plan, blocked := reshapePlan()
	cases := map[string]struct {
		id int32
		to Region
	}{
		"door would stand inside the room": {1, rect(10, 10, 6, 6)},
		"door would be off the walls":      {1, rect(10, 10, 6, 3)},
		"into another room's walls":        {1, rect(10, 10, 19, 4)},
		"off the core":                     {1, rect(10, 10, 6, 4).Translate(-12, 0)},
		"unknown room":                     {9, rect(10, 10, 8, 4)},
		"no change":                        {1, rect(10, 10, 6, 4)},
		"not a rectangle":                  {1, RegionOf([]domain.Cell{{X: 10, Z: 10}, {X: 11, Z: 10}, {X: 10, Z: 11}})},
	}
	for name, c := range cases {
		got, _, ok := ReshapeRoom(plan, c.id, c.to, blocked)
		if ok || !got.Rooms[0].Same(plan.Rooms[0]) {
			t.Errorf("%s: reshaped (%v)", name, ok)
		}
	}
	if _, _, ok := ReshapeRoom(plan, 1, rect(10, 10, 8, 4), map[domain.Cell]bool{{X: 17, Z: 11}: true}); ok {
		t.Error("reshaped over a blocked cell")
	}
	// A hallway in the way.
	hall := plan
	hall.Spine = []SpineSegment{{From: domain.Cell{X: 18, Z: 5}, To: domain.Cell{X: 18, Z: 20}}}
	if _, _, ok := ReshapeRoom(hall, 1, rect(10, 10, 8, 4), nil); ok {
		t.Error("reshaped into a hallway")
	}
}

func TestReshapeOutgrownEnlargesABuiltRoomOnlyWhenNothingElseIsRebuilt(t *testing.T) {
	plan, _ := reshapePlan()
	s := Successor{Replaces: 1, Role: PlannedStorage, Floor: 24, Shortfall: 10}
	growth := RoomGrowth{Built: map[int32]bool{1: true, 2: true}, Fixed: map[int32]bool{1: true, 2: true}}
	next, ok := reshapeOutgrown(plan, s, growth, nil)
	if !ok {
		t.Fatal("not enlarged")
	}
	// 10 more cells on a 4-deep room: two columns (8) is short, three (12) fits;
	// the fewest cells over, east before west.
	if got := next.Rooms[0].Region; !got.Equal(rect(10, 10, 9, 4)) {
		t.Fatalf("region = %v", got.Bounds())
	}
	// The room must be built, and no other room may be mid-rebuild.
	if _, ok := reshapeOutgrown(plan, s, RoomGrowth{Built: map[int32]bool{2: true}, Fixed: map[int32]bool{2: true}}, nil); ok {
		t.Error("an unbuilt room was reshaped")
	}
	if _, ok := reshapeOutgrown(plan, s, RoomGrowth{Built: map[int32]bool{1: true}, Fixed: map[int32]bool{1: true, 2: true}}, nil); ok {
		t.Error("reshaped while room 2 was being built")
	}
	if _, ok := reshapeOutgrown(plan, s, RoomGrowth{}, nil); ok {
		t.Error("reshaped with the census unknown")
	}
	// East blocked: the west takes it.
	west, ok := reshapeOutgrown(plan, s, growth, map[domain.Cell]bool{{X: 17, Z: 11}: true})
	if !ok || !west.Rooms[0].Region.Equal(rect(7, 10, 9, 4)) {
		t.Fatalf("west = %v, %v", ok, west.Rooms[0].Region.Bounds())
	}
}

func TestReplanEnlargesAnOutgrownBuiltRoomWithoutASuccessor(t *testing.T) {
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
	ask := Successor{Replaces: old.ID, Role: PlannedStorage, Floor: old.Region.Area(), Shortfall: 4, Sizes: [][2]int32{{16, 9}}}
	next, changed, err := ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: all, Built: all, Successor: ask}, 0, 3, 0, TechTierCamp, nil, nil)
	if err != nil || !changed || !next.Valid() {
		t.Fatalf("changed %v valid %v err %v", changed, next.Valid(), err)
	}
	got := ReshapedRooms(plan, next)
	if len(got) != 1 || got[0].Room.ID != old.ID || got[0].Room.Region.Area() < old.Region.Area()+4 {
		t.Fatalf("reshaped = %v, want the storeroom enlarged by 4", got)
	}
	if planned, _ := SuccessionChanges(plan, next); len(planned) != 0 || SuccessorPending(next) {
		t.Fatal("an in-place enlargement also planned a successor")
	}
}
