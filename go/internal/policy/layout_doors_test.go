package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// doorPair is a freezer (west) and a dining room (east) sharing the wall x=5,
// the freezer's door into dining at (5,2) held as a RoomLink in the door graph.
func doorPair() LayoutPlan {
	link := domain.Cell{X: 5, Z: 2}
	freezer := PlannedRoom{ID: 1, Role: PlannedFreezer, Region: RectRegion(Rectangle{X: 1, Z: 1, Width: 4, Height: 4}), Door: domain.Cell{X: 2, Z: 0}, DoorRot: domain.South}
	dining := PlannedRoom{ID: 2, Role: PlannedDining, Region: RectRegion(Rectangle{X: 6, Z: 1, Width: 5, Height: 4}), Door: domain.Cell{X: 8, Z: 0}, DoorRot: domain.South}
	return LayoutPlan{Rooms: []PlannedRoom{freezer, dining}, Links: []RoomLink{{A: 1, B: 2, Cell: link}}}
}

// TestDoorsOfGivesDoorThenLinks: a room's doors are its own Door, then the
// links the door graph gives it, each cell once; the neighbour reads its side
// of the shared door.
func TestDoorsOfGivesDoorThenLinks(t *testing.T) {
	p := doorPair()
	if got := p.DoorsOf(p.Rooms[1]); !slices.Contains(got, domain.Cell{X: 5, Z: 2}) {
		t.Fatalf("dining doors %v lack the link", got)
	}
	if got := p.DoorsOf(p.Rooms[0]); !slices.Equal(got, []domain.Cell{{X: 2, Z: 0}, {X: 5, Z: 2}}) {
		t.Fatalf("freezer doors %v: want Door then its link", got)
	}
	// A link that lies on the room's own door cell is listed once.
	dup := p
	dup.Links = []RoomLink{{A: 1, B: 2, Cell: p.Rooms[0].Door}}
	if got := dup.DoorsOf(dup.Rooms[0]); len(got) != 1 {
		t.Fatalf("doors %v: a repeated cell must be listed once", got)
	}
}

// TestShellDoorsOnTiledPlan: the shell family reads link doors from the door
// graph on both sides of the shared wall.
func TestShellDoorsOnTiledPlan(t *testing.T) {
	p := doorPair()
	for i := range p.Rooms {
		if got := p.ShellDoors(p.Rooms[i]); !slices.Contains(got, domain.Cell{X: 5, Z: 2}) {
			t.Fatalf("room %d: shell doors %v lack the shared link", p.Rooms[i].ID, got)
		}
	}
}

// TestRouteWalkOnTiledPlan: a link door is walkable ground between its rooms.
func TestRouteWalkOnTiledPlan(t *testing.T) {
	link := domain.Cell{X: 5, Z: 2}
	walk, _ := routeWalk(doorPair())
	if i, ok := walk[link]; !ok || i != -1 {
		t.Fatalf("link cell walk %d (present %v), want -1", i, ok)
	}
}

// TestFootprintOnTiledPlan: a tiled room shells from its own door; the link
// it shares with a neighbour is a ShellDoors cell, not a footprint door.
func TestFootprintOnTiledPlan(t *testing.T) {
	room := doorPair().Rooms[1]
	shell, err := room.Footprint()
	if err != nil {
		t.Fatal(err)
	}
	if got := len(shell.ExtraDoors()); got != 0 {
		t.Fatalf("footprint extra doors %d, want none", got)
	}
}

// TestFreezerDoorsOnTiledPlan: the freezer's door into dining and the
// butchery's into the freezer resolve through the door graph.
func TestFreezerDoorsOnTiledPlan(t *testing.T) {
	tiled := doorPair()
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
