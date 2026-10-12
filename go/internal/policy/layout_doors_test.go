package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// doorPair is a freezer (west) and a dining room (east) sharing the wall x=5,
// the freezer's door into dining at (5,2). The same pair is built twice: as the
// old generator writes it (the freezer's Link) and as the tiler writes it (a
// RoomLink in the door graph, no Link).
func doorPair() (old, tiled LayoutPlan) {
	link := domain.Cell{X: 5, Z: 2}
	freezer := PlannedRoom{ID: 1, Role: PlannedFreezer, Region: RectRegion(Rectangle{X: 1, Z: 1, Width: 4, Height: 4}), Door: domain.Cell{X: 2, Z: 0}, DoorRot: domain.South}
	dining := PlannedRoom{ID: 2, Role: PlannedDining, Region: RectRegion(Rectangle{X: 6, Z: 1, Width: 5, Height: 4}), Door: domain.Cell{X: 8, Z: 0}, DoorRot: domain.South}
	withLink := freezer
	withLink.Link = &link
	old = LayoutPlan{Rooms: []PlannedRoom{withLink, dining}}
	tiled = LayoutPlan{Rooms: []PlannedRoom{freezer, dining}, Links: []RoomLink{{A: 1, B: 2, Cell: link}}}
	return old, tiled
}

func ordered(cs []domain.Cell) []domain.Cell {
	out := slices.Clone(cs)
	slices.SortFunc(out, func(a, b domain.Cell) int {
		if a.X != b.X {
			return int(a.X - b.X)
		}
		return int(a.Z - b.Z)
	})
	return out
}

// TestDoorsOfAgreesAcrossPlans: the room that holds the link reads the same
// from the old generator's Link and from the tiler's door graph, each cell
// once. The tiled graph also gives the neighbour its side of the door; the old
// plan keeps that on the other room's Link (ShellDoors reads it there).
func TestDoorsOfAgreesAcrossPlans(t *testing.T) {
	old, tiled := doorPair()
	if got, want := ordered(tiled.DoorsOf(tiled.Rooms[0])), ordered(old.DoorsOf(old.Rooms[0])); !slices.Equal(got, want) {
		t.Fatalf("tiled freezer doors %v, old %v", got, want)
	}
	if got := tiled.DoorsOf(tiled.Rooms[1]); !slices.Contains(got, domain.Cell{X: 5, Z: 2}) {
		t.Fatalf("tiled dining doors %v lack the link", got)
	}
	freezer := tiled.Rooms[0]
	if got := tiled.DoorsOf(freezer); !slices.Equal(got, []domain.Cell{{X: 2, Z: 0}, {X: 5, Z: 2}}) {
		t.Fatalf("freezer doors %v: want Door then its link", got)
	}
	// A cell named by Door, Doors and a link is listed once.
	dup := freezer
	dup.Doors = []Door{{Cell: freezer.Door, Rot: freezer.DoorRot}, {Cell: domain.Cell{X: 5, Z: 2}, Rot: domain.East}}
	if got := tiled.DoorsOf(dup); len(got) != 2 {
		t.Fatalf("doors %v: a repeated cell must be listed once", got)
	}
}

// TestShellDoorsOnTiledPlan: the shell family reads link doors from the door
// graph on both sides of the shared wall, as it does from Link.
func TestShellDoorsOnTiledPlan(t *testing.T) {
	old, tiled := doorPair()
	for i := range old.Rooms {
		got, want := ordered(tiled.ShellDoors(tiled.Rooms[i])), ordered(old.ShellDoors(old.Rooms[i]))
		if !slices.Equal(got, want) {
			t.Fatalf("room %d: tiled shell doors %v, old %v", old.Rooms[i].ID, got, want)
		}
		if !slices.Contains(got, domain.Cell{X: 5, Z: 2}) {
			t.Fatalf("room %d: shell doors %v lack the shared link", old.Rooms[i].ID, got)
		}
	}
}

// TestRouteWalkOnTiledPlan: a link door is walkable ground between its rooms,
// the same cell as when the freezer holds it as Link.
func TestRouteWalkOnTiledPlan(t *testing.T) {
	old, tiled := doorPair()
	link := domain.Cell{X: 5, Z: 2}
	for name, p := range map[string]LayoutPlan{"old": old, "tiled": tiled} {
		walk, _ := routeWalk(p)
		if i, ok := walk[link]; !ok || i != -1 {
			t.Fatalf("%s plan: link cell walk %d (present %v), want -1", name, i, ok)
		}
	}
}

// TestFootprintOnTiledPlan: a tiled room shells from its own doors; the link
// it shares with a neighbour is a ShellDoors cell, not a footprint door.
func TestFootprintOnTiledPlan(t *testing.T) {
	_, tiled := doorPair()
	room := tiled.Rooms[1]
	room.Doors = []Door{{Cell: domain.Cell{X: 11, Z: 3}, Rot: domain.East}}
	shell, err := room.Footprint()
	if err != nil {
		t.Fatal(err)
	}
	if got := len(shell.ExtraDoors()); got != 1 {
		t.Fatalf("footprint extra doors %d, want the one boundary door", got)
	}
	if got := room.BoundaryDoors(); len(got) != 2 || got[0].Cell != room.Door || got[1].Cell != room.Doors[0].Cell {
		t.Fatalf("boundary doors %v", got)
	}
}

// TestFreezerDoorsOnTiledPlan: the freezer's door into dining and the
// butchery's into the freezer resolve through the door graph.
func TestFreezerDoorsOnTiledPlan(t *testing.T) {
	_, tiled := doorPair()
	if _, door, ok := tiled.FreezerDoorInto(tiled.Rooms[1]); !ok || door != (domain.Cell{X: 5, Z: 2}) {
		t.Fatalf("freezer door into dining %v %v", door, ok)
	}
	// A butchery north of the freezer, its door the link in the freezer's wall.
	butchery := PlannedRoom{ID: 3, Role: PlannedButchery, Region: RectRegion(Rectangle{X: 1, Z: -4, Width: 3, Height: 3}), Door: domain.Cell{X: 0, Z: -3}, DoorRot: domain.East}
	tiled.Rooms = append(tiled.Rooms, butchery)
	tiled.Links = append(tiled.Links, RoomLink{A: 1, B: 3, Cell: domain.Cell{X: 1, Z: 0}})
	if c, ok := butcheryDoor(tiled); !ok || c != (domain.Cell{X: 1, Z: 0}) {
		t.Fatalf("butchery door %v %v", c, ok)
	}
}
