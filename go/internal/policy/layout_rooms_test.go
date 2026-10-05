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

// plannedGround is a bounds-sized open lit field and the spine hallway
// the planned rooms' doors open onto, protected.
func plannedGround(bounds Bounds, edit func(*SiteCell)) ([]SiteCell, []domain.Cell) {
	var cells []SiteCell
	for x := int32(0); x < bounds.Width; x++ {
		for z := int32(0); z < bounds.Height; z++ {
			c := SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), SupportsLight: domain.Known(true)}
			if edit != nil {
				edit(&c)
			}
			cells = append(cells, c)
		}
	}
	var hallway []domain.Cell
	for x := int32(10); x < 40; x++ {
		for z := int32(29); z <= 31; z++ {
			hallway = append(hallway, domain.Cell{X: x, Z: z})
		}
	}
	return cells, hallway
}

// #1231: a shell builds the first buildable planned room of its role, and
// with none buildable (or none planned) there is no site at all.
func TestPlannedLayoutBuildsTheFirstBuildablePlannedRoom(t *testing.T) {
	bounds := Bounds{Width: 60, Height: 60}
	taken := hallRoom(PlannedWorkshop, 20, 30, 7, 5, true)
	next := hallRoom(PlannedWorkshop, 20, 30, 7, 5, false)
	cells, hallway := plannedGround(bounds, func(c *SiteCell) {
		in := taken.Interior
		c.Occupied = domain.Known(c.Cell.X >= in.X && c.Cell.X < in.X+in.Width && c.Cell.Z >= in.Z && c.Cell.Z < in.Z+in.Height)
	})
	plan := LayoutPlan{Rooms: []PlannedRoom{taken, next}}
	layout, ok, err := PlannedLayout(StarterRequest{Bounds: bounds, Cells: cells, Protected: hallway, Planned: plan.PlannedShells(RoomRoleWorkshop)})
	if err != nil || !ok {
		t.Fatalf("no planned layout: %v", err)
	}
	want, _ := next.Footprint()
	if !domain.SameRoomFootprint(layout.Shell, want) || layout.Shell.Door() != next.Door || layout.Planned != 1 {
		t.Fatalf("built %+v door %v index %d, want the planned room door %v", layout.Shell.Bounds(), layout.Shell.Door(), layout.Planned, next.Door)
	}
	if _, ok, err := PlannedLayout(StarterRequest{Bounds: bounds, Cells: cells, Protected: hallway, Planned: plan.PlannedShells(RoomRoleWorkshop)[:1]}); err != nil || ok {
		t.Fatalf("a blocked planned room was sited (%v)", err)
	}
	if _, ok, err := PlannedLayout(StarterRequest{Bounds: bounds, Cells: cells, Protected: hallway}); err != nil || ok {
		t.Fatalf("a shell was sited with no planned room (%v)", err)
	}
}

// The initial shelter is the plan's storeroom at Camp (#1177, #1231): its
// rectangle and spine door, bunks clear of the door aisle; blocked, it is
// refused rather than searched for elsewhere.
func TestShelterStandsOnThePlannedStoreroom(t *testing.T) {
	bounds := Bounds{Width: 60, Height: 60}
	storage := hallRoom(PlannedStorage, 20, 30, 9, 7, true)
	plan := LayoutPlan{Rooms: []PlannedRoom{hallRoom(PlannedKitchen, 10, 30, 6, 5, true), storage}}
	cells, hallway := plannedGround(bounds, nil)
	request := StarterRequest{Bounds: bounds, Cells: cells, Protected: hallway, Planned: plan.PlannedShells(RoomRoleStoreroom)}
	layout, ok, err := PlannedLayout(request)
	if err != nil || !ok {
		t.Fatalf("no storeroom layout: %v", err)
	}
	want, _ := storage.Footprint()
	if !domain.SameRoomFootprint(layout.Shell, want) || layout.Shell.Door() != storage.Door {
		t.Fatalf("shell %+v door %v, want storage %+v door %v", layout.Shell.Bounds(), layout.Shell.Door(), want.Bounds(), storage.Door)
	}
	inward := domain.Cell{X: storage.Door.X, Z: storage.Door.Z + 1}
	bunks := PlanShelterBunks(layout, testShapes, 3, 1, 0, nil)
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
	request.Cells, _ = plannedGround(bounds, func(c *SiteCell) {
		c.Occupied = domain.Known(c.Cell == domain.Cell{X: storage.Interior.X + 3, Z: storage.Interior.Z + 3})
	})
	if _, ok, err := PlannedLayout(request); err != nil || ok {
		t.Fatalf("a blocked storeroom was sited (%v)", err)
	}
}

// #718, #1231: any planned room's ring claims a claimable ruin of its own
// wall kind and keeps rock as wall; rock inside is left to plan dig; a ruin
// of another kind, or one under an ancient danger, blocks the room until
// home clearance removes it.
func TestPlannedLayoutClaimsMatchingRuinsAndMarksRock(t *testing.T) {
	bounds := Bounds{Width: 60, Height: 60}
	room := hallRoom(PlannedWorkshop, 20, 30, 7, 5, true)
	shell, _ := room.Footprint()
	b := shell.Bounds()
	ruin := domain.Cell{X: b.X + 2, Z: b.Z + b.Height - 1}
	rock := domain.Cell{X: b.X + b.Width - 1, Z: b.Z + 2}
	inner := domain.Cell{X: room.Interior.X + 1, Z: room.Interior.Z + 1}
	ground := func(def, hold string) []SiteCell {
		cells, _ := plannedGround(bounds, func(c *SiteCell) {
			switch c.Cell {
			case ruin:
				c.Occupied, c.Ruin, c.ClaimableRuin, c.RuinHold = domain.Known(true), domain.Known(true), domain.Known(def), hold
			case rock, inner:
				c.Walkable, c.Occupied, c.NaturalRock = domain.Known(false), domain.Known(true), domain.Known(true)
			}
		})
		return cells
	}
	_, hallway := plannedGround(bounds, nil)
	plan := LayoutPlan{Rooms: []PlannedRoom{room}}
	request := StarterRequest{Bounds: bounds, Cells: ground("Wall", ""), Protected: hallway, WallDef: "Wall", Planned: plan.PlannedShells(RoomRoleWorkshop)}
	layout, ok, err := PlannedLayout(request)
	if err != nil || !ok {
		t.Fatalf("no layout: %v", err)
	}
	if len(layout.Claimed) != 1 || layout.Claimed[0] != ruin || len(layout.Reused) != 1 || layout.Reused[0] != rock || len(layout.Mined) != 1 || layout.Mined[0] != inner {
		t.Fatalf("claimed %v reused %v mined %v", layout.Claimed, layout.Reused, layout.Mined)
	}
	for _, blocked := range []struct{ def, hold string }{{"Wall", "ancient_danger"}, {"SandbagWall", ""}, {"", ""}} {
		request.Cells = ground(blocked.def, blocked.hold)
		if _, ok, err := PlannedLayout(request); err != nil || ok {
			t.Fatalf("ruin %+v on the ring did not block the room (%v)", blocked, err)
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
