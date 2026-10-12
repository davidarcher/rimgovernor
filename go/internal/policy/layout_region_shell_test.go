package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rectsRegion(rs ...Rectangle) Region {
	var cells []domain.Cell
	for _, r := range rs {
		cells = append(cells, rectCells(r)...)
	}
	return RegionOf(cells)
}

// shapedRoom is a storage room over region with a door the footprint rules
// accept: the first wall cell and entrance side that form a valid boundary door.
func shapedRoom(t *testing.T, region Region) PlannedRoom {
	t.Helper()
	r := PlannedRoom{Role: PlannedStorage, Region: region}
	for _, w := range region.Walls().Cells() {
		for _, rot := range []domain.Rotation{domain.North, domain.East, domain.South, domain.West} {
			r.Door, r.DoorRot = w, rot
			if _, err := r.Footprint(); err == nil {
				return r
			}
		}
	}
	t.Fatal("no door site")
	return r
}

func irregularRegions() map[string]Region {
	return map[string]Region{
		"L":     rectsRegion(Rectangle{X: 20, Z: 30, Width: 8, Height: 4}, Rectangle{X: 20, Z: 34, Width: 4, Height: 4}),
		"T":     rectsRegion(Rectangle{X: 20, Z: 30, Width: 9, Height: 3}, Rectangle{X: 23, Z: 33, Width: 3, Height: 4}),
		"holed": rectsRegion(Rectangle{X: 20, Z: 30, Width: 7, Height: 7}).minus(rectsRegion(Rectangle{X: 23, Z: 33, Width: 1, Height: 1})),
		"rect":  rectsRegion(Rectangle{X: 20, Z: 30, Width: 6, Height: 5}),
	}
}

func TestGroundMatchesOnIrregularRegions(t *testing.T) {
	for name, region := range irregularRegions() {
		t.Run(name, func(t *testing.T) {
			room := shapedRoom(t, region)
			plan := LayoutPlan{Rooms: []PlannedRoom{room}}
			ring := room.wallRing()
			if !slices.Equal(ring.Cells(), region.Walls().Cells()) {
				t.Fatal("the ring is the region's wall ring")
			}
			if !plan.GroundMatches(room, ringWalls(plan, room)) {
				t.Fatal("a complete ring matches")
			}
			if plan.GroundMatches(room, GroundCensus{}) {
				t.Fatal("empty ground does not match")
			}
			// Every ring cell matters, the notch and hole walls included.
			for _, c := range ring.Cells() {
				g := ringWalls(plan, room)
				delete(g.walls, c)
				delete(g.doors, c)
				if plan.GroundMatches(room, g) {
					t.Fatalf("a missing %v does not match", c)
				}
			}
			// A door where the plan holds a wall does not match.
			for _, c := range ring.Cells() {
				if c == room.Door {
					continue
				}
				g := ringWalls(plan, room)
				delete(g.walls, c)
				g.doors[c] = true
				if plan.GroundMatches(room, g) {
					t.Fatalf("a door at %v does not match", c)
				}
				break
			}
			if !slices.Contains(plan.ShellDoors(room), room.Door) {
				t.Fatal("the plan's door takes a door")
			}
			shell, err := room.Footprint()
			if err != nil {
				t.Fatal(err)
			}
			if !slices.Equal(shell.Interior(), region.Cells()) && len(shell.Interior()) != region.Area() {
				t.Fatal("the shell interior is the region")
			}
			if len(shell.Walls()) != len(ring.Cells()) {
				t.Fatal("the shell ring is the region's ring")
			}
		})
	}
}

func TestShellDoorsTakeAnotherRoomsLinkOnAnIrregularRing(t *testing.T) {
	region := irregularRegions()["L"]
	room := shapedRoom(t, region)
	other := hallRoom(PlannedFreezer, 40, 30, 4, 4, true)
	side := domain.Cell{X: 24, Z: 35} // beside the L's inner corner
	if !region.Walls().Contains(side) || !region.adjacent(side) {
		t.Fatalf("%v is not a side wall cell", side)
	}
	plan := LayoutPlan{Rooms: []PlannedRoom{room, other}, Links: []RoomLink{{A: 1, B: 2, Cell: side}}}.WithRoomIDs()
	if !slices.Contains(plan.ShellDoors(room), side) {
		t.Fatal("a link on the ring takes a door")
	}
	corner := domain.Cell{X: 28, Z: 34} // diagonal to the arm's corner
	if !region.Walls().Contains(corner) || region.adjacent(corner) {
		t.Fatalf("%v is not a diagonal corner", corner)
	}
	plan.Links = []RoomLink{{A: 1, B: 2, Cell: corner}}
	if slices.Contains(plan.ShellDoors(room), corner) {
		t.Fatal("a link on a ring corner takes no door")
	}
}

func TestCensusRoomInComparesCellSets(t *testing.T) {
	enclosed := func(cells []domain.Cell) RoomObservation {
		return RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "r", Enclosed: domain.Known(true), Cells: cells}}}
	}
	for name, region := range irregularRegions() {
		t.Run(name, func(t *testing.T) {
			room := shapedRoom(t, region)
			if _, ok := CensusRoomIn(room, enclosed(region.Cells())); !ok {
				t.Fatal("a census room matching the region stands")
			}
			if !region.Contains(region.anchor()) {
				t.Fatal("the anchor lies in the region")
			}
			if _, ok := CensusRoomIn(room, enclosed(region.Translate(1, 0).Cells())); ok {
				t.Fatal("a census room one cell off is not the planned room")
			}
			b := region.Bounds()
			spill := append(region.Cells(), domain.Cell{X: b.X + b.Width, Z: b.Z + b.Height})
			if _, ok := CensusRoomIn(room, enclosed(spill)); ok {
				t.Fatal("a census room spilling past the region is not the planned room")
			}
			open := enclosed(region.Cells())
			open.Rooms[0].Enclosed = domain.Known(false)
			if _, ok := CensusRoomIn(room, open); ok {
				t.Fatal("an unenclosed room does not stand")
			}
		})
	}
}
