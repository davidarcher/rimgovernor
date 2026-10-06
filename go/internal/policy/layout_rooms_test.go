package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// hallRoom is a room of role at interior X ix on the east-west hallway at z0,
// north of it or south, its door in the hallway wall.
func hallRoom(role PlannedRole, ix, z0, w, d int32, north bool) PlannedRoom {
	return hallFrame{along: true, line: z0}.room(role, ix, w, d, north)
}

func TestPlannedShellsKeepThePlanRectangleAndDoor(t *testing.T) {
	north := hallRoom(PlannedWorkshop, 20, 30, 7, 5, true)
	south := hallRoom(PlannedWorkshop, 30, 30, 7, 5, false)
	plan := LayoutPlan{Rooms: []PlannedRoom{hallRoom(PlannedKitchen, 10, 30, 6, 5, true), north, south}}
	shells := plan.PlannedShells(RoomRoleWorkshop)
	if len(shells) != 2 {
		t.Fatalf("%d workshop shells", len(shells))
	}
	for i, r := range []PlannedRoom{north, south} {
		s := shells[i]
		b := s.Bounds()
		if s.Door() != r.Door || s.Entrance() != r.DoorRot || b.X != r.Interior.X-1 || b.Z != r.Interior.Z-1 || b.Width != r.Interior.Width+2 || b.Height != r.Interior.Height+2 {
			t.Fatalf("shell %d: door %v %v bounds %+v, room %+v", i, s.Door(), s.Entrance(), b, r)
		}
	}
	if len(plan.PlannedShells(RoomRoleTomb)) != 0 {
		t.Fatal("a role the core plans no room for got shells")
	}
}

// The initial shelter is the plan's storeroom at Camp (#1177, #1231): its
// rectangle and spine door, bunks clear of the door aisle.
func TestShelterBunksStandInThePlannedStoreroomClearOfTheDoorAisle(t *testing.T) {
	storage := hallRoom(PlannedStorage, 20, 30, 9, 7, true)
	inward := domain.Cell{X: storage.Door.X, Z: storage.Door.Z + 1}
	bunks := PlanShelterBunks(storage, nil, testShapes, 3, 1, 0, nil)
	if len(bunks) != 3 {
		t.Fatalf("%d bunks fit", len(bunks))
	}
	for _, bunk := range bunks {
		for _, c := range rectCells(bunk.Rect) {
			if c == inward {
				t.Fatalf("bunk at %v blocks the door aisle %v", bunk.Anchor(), inward)
			}
		}
	}
}

func TestShellDoorsPutTheFreezerLinkInBothRings(t *testing.T) {
	kitchen := hallRoom(PlannedKitchen, 10, 30, 6, 5, true)
	freezer := hallRoom(PlannedFreezer, 17, 30, 5, 5, true)
	link := domain.Cell{X: 16, Z: kitchen.Interior.Z + 2}
	freezer.Link = &link
	jail := hallRoom(PlannedPrison, 40, 30, 5, 5, true)
	plan := LayoutPlan{Rooms: []PlannedRoom{kitchen, freezer, jail}}
	has := func(doors []domain.Cell, c domain.Cell) bool {
		for _, d := range doors {
			if d == c {
				return true
			}
		}
		return false
	}
	if d := plan.ShellDoors(kitchen); len(d) != 2 || !has(d, kitchen.Door) || !has(d, link) {
		t.Fatalf("kitchen doors %v", d)
	}
	if d := plan.ShellDoors(freezer); len(d) != 2 || !has(d, freezer.Door) || !has(d, link) {
		t.Fatalf("freezer doors %v", d)
	}
	if d := plan.ShellDoors(jail); len(d) != 1 || d[0] != jail.Door {
		t.Fatalf("jail doors %v", d)
	}
}

func TestNextPlannedRoomSkipsStandingRooms(t *testing.T) {
	built := hallRoom(PlannedPrison, 20, 30, 5, 5, true)
	open := hallRoom(PlannedPrison, 30, 30, 5, 5, true)
	plan := LayoutPlan{Rooms: []PlannedRoom{built, open}}
	ground := ringWalls(plan, built)
	if r, ok := plan.NextPlannedRoom(PlannedPrison, ground); !ok || r.Interior != open.Interior {
		t.Fatalf("next %+v %v", r, ok)
	}
	if _, ok := plan.NextPlannedRoom(PlannedKitchen, ground); ok {
		t.Fatal("no kitchen is planned")
	}
}

// ringWalls is the ground census of plan's room r with its ring standing as
// planned: a wall on every ring cell, a door on each of its door cells.
func ringWalls(plan LayoutPlan, r PlannedRoom) GroundCensus {
	g := GroundCensus{walls: map[domain.Cell]bool{}, doors: map[domain.Cell]bool{}, flaps: map[domain.Cell]bool{}}
	doors := map[domain.Cell]bool{}
	for _, d := range plan.ShellDoors(r) {
		doors[d] = true
	}
	ring := roomWalls(r)
	for _, c := range rectCells(ring) {
		switch {
		case !onRing(c, ring):
		case slices.Contains(plan.FlapCells(r), c):
			g.flaps[c] = true
		case doors[c]:
			g.doors[c] = true
		default:
			g.walls[c] = true
		}
	}
	return g
}

func TestGroundMatches(t *testing.T) {
	room := hallRoom(PlannedPrison, 20, 30, 5, 5, true)
	plan := LayoutPlan{Rooms: []PlannedRoom{room}}
	ring := func() GroundCensus { return ringWalls(plan, room) }
	// A ring standing matches while the census lists no enclosed room yet.
	if !plan.GroundMatches(room, ring()) {
		t.Fatal("a complete ring matches")
	}
	if _, ok := CensusRoomIn(room, RoomObservation{Shapes: testShapes}); ok {
		t.Fatal("no census room stands")
	}
	if plan.GroundMatches(room, GroundCensus{}) {
		t.Fatal("empty ground does not match")
	}
	missing := ring()
	delete(missing.walls, domain.Cell{X: room.Interior.X - 1, Z: room.Interior.Z + 1})
	if plan.GroundMatches(room, missing) {
		t.Fatal("a missing wall cell does not match")
	}
	extra, c := ring(), domain.Cell{X: room.Interior.X - 1, Z: room.Interior.Z + 1}
	delete(extra.walls, c)
	extra.doors[c] = true
	if plan.GroundMatches(room, extra) {
		t.Fatal("an extra door on the ring does not match")
	}
	wrong := ring()
	delete(wrong.doors, room.Door)
	wrong.walls[room.Door] = true
	if plan.GroundMatches(room, wrong) {
		t.Fatal("a wall where the plan has the door does not match")
	}
	gap := ring()
	delete(gap.doors, room.Door)
	if plan.GroundMatches(room, gap) {
		t.Fatal("a missing door does not match")
	}
}

func TestGroundOfReadsWallsAndDoors(t *testing.T) {
	building := func(def string, x int32) CurrentBuilding {
		b, err := domain.NewBuilding(def, domain.Cell{X: x, Z: 1}, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		return CurrentBuilding{Building: b, Cells: []domain.Cell{{X: x, Z: 1}}}
	}
	g := GroundOf([]CurrentBuilding{building("Wall", 1), building("Door", 2), building("Autodoor", 3), building("Bed", 4)})
	if !g.walls[domain.Cell{X: 1, Z: 1}] || !g.doors[domain.Cell{X: 2, Z: 1}] || !g.doors[domain.Cell{X: 3, Z: 1}] || len(g.walls)+len(g.doors) != 3 {
		t.Fatalf("ground %+v", g)
	}
}
