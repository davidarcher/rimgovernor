package domain

import (
	"math/rand"
	"testing"
)

func cellSet(cells []Cell) map[Cell]bool {
	set := map[Cell]bool{}
	for _, c := range cells {
		set[c] = true
	}
	return set
}

// A footprint is enclosed when no interior cell has a neighbour, orthogonal or
// diagonal, outside interior and wall together.
func assertEnclosed(t *testing.T, f RoomFootprint) {
	t.Helper()
	inside, walls := cellSet(f.Interior()), cellSet(f.Walls())
	for c := range inside {
		for _, n := range neighbours8(c) {
			if !inside[n] && !walls[n] {
				t.Fatalf("interior %v has open neighbour %v", c, n)
			}
		}
		if walls[c] {
			t.Fatalf("cell %v is both interior and wall", c)
		}
	}
	for w := range walls {
		touches := false
		for _, n := range neighbours8(w) {
			touches = touches || inside[n]
		}
		if !touches {
			t.Fatalf("wall %v touches no interior cell", w)
		}
	}
	if !walls[f.Door()] {
		t.Fatalf("door %v is not a wall cell", f.Door())
	}
}

// testRoofSupport is Core's RoofCollapseUtility.RoofMaxSupportDistance.
const testRoofSupport = 6.9

func TestRectangleFootprintIsThePerimeter(t *testing.T) {
	bounds := RoomBounds{X: 3, Z: 5, Width: 9, Height: 7}
	for _, entrance := range []Rotation{North, East, South, West} {
		f, err := RectangleFootprint(bounds, entrance)
		if err != nil {
			t.Fatal(err)
		}
		if len(f.Interior()) != 7*5 || len(f.Walls()) != 2*9+2*5 {
			t.Fatalf("interior %d walls %d", len(f.Interior()), len(f.Walls()))
		}
		if f.Bounds() != bounds {
			t.Fatalf("bounds %v", f.Bounds())
		}
		if !f.RoofSupported(testRoofSupport) {
			t.Fatal("9x7 rectangle must be roof supported")
		}
		assertEnclosed(t, f)
		var wantDoor Cell
		switch entrance {
		case North:
			wantDoor = Cell{X: bounds.X + bounds.Width/2, Z: bounds.Z + bounds.Height - 1}
		case South:
			wantDoor = Cell{X: bounds.X + bounds.Width/2, Z: bounds.Z}
		case East:
			wantDoor = Cell{X: bounds.X + bounds.Width - 1, Z: bounds.Z + bounds.Height/2}
		case West:
			wantDoor = Cell{X: bounds.X, Z: bounds.Z + bounds.Height/2}
		}
		if f.Door() != wantDoor {
			t.Fatalf("%s door %v want %v", entrance, f.Door(), wantDoor)
		}
		got := f.Placements("Wall", "Door", "WoodLog")
		if len(got) != len(f.Walls()) || got[0].Cell() != f.Door() || got[0].Definition() != "Door" || got[0].Rotation() != entrance {
			t.Fatalf("placements %d walls %d first %v", len(got), len(f.Walls()), got[0])
		}
		// Walls follow the door in (z outer, x inner) order.
		for i := 2; i < len(got); i++ {
			a, b := got[i-1].Cell(), got[i].Cell()
			if a.Z > b.Z || (a.Z == b.Z && a.X >= b.X) {
				t.Fatalf("%s placement %d %v is out of order after %v", entrance, i, b, a)
			}
		}
	}
}

func TestNewRoomFootprintRejectsInvalidGeometry(t *testing.T) {
	square := []Cell{{2, 2}, {3, 2}, {2, 3}, {3, 3}}
	cases := map[string]struct {
		interior []Cell
		door     Cell
		entrance Rotation
	}{
		"empty":             {nil, Cell{2, 1}, South},
		"bad entrance":      {square, Cell{2, 1}, Rotation("up")},
		"duplicate":         {append(append([]Cell(nil), square...), Cell{2, 2}), Cell{2, 1}, South},
		"map edge":          {[]Cell{{0, 2}, {1, 2}}, Cell{0, 1}, South},
		"disconnected":      {[]Cell{{2, 2}, {4, 2}}, Cell{2, 1}, South},
		"door not wall":     {square, Cell{5, 5}, South},
		"door is corner":    {square, Cell{1, 1}, South},
		"door faces inward": {square, Cell{2, 1}, North},
		"door side wrong":   {square, Cell{2, 1}, East},
	}
	for name, tc := range cases {
		if _, err := NewRoomFootprint(tc.interior, tc.door, tc.entrance); err == nil {
			t.Errorf("%s: expected error", name)
		}
	}
	if _, err := NewRoomFootprint(square, Cell{2, 1}, South); err != nil {
		t.Fatal(err)
	}
}

func TestGrowFootprintHugsConstrainedTerrain(t *testing.T) {
	// A three-wide corridor of free cells (x 4..6 inclusive is interior room,
	// walls need x 3 and 7) running north from z 3, blocked elsewhere.
	free := func(c Cell) bool { return c.X >= 3 && c.X <= 7 && c.Z >= 3 && c.Z <= 40 }
	f, ok := GrowFootprint(Cell{5, 10}, free, 49, testRoofSupport)
	if !ok {
		t.Fatal("corridor room not grown")
	}
	assertEnclosed(t, f)
	if len(f.Interior()) != 49 || !f.RoofSupported(testRoofSupport) {
		t.Fatalf("interior %d supported %v", len(f.Interior()), f.RoofSupported(testRoofSupport))
	}
	for _, c := range f.Cells() {
		if !free(c) {
			t.Fatalf("shell cell %v is not free", c)
		}
	}
	if b := f.Bounds(); b.Width != 5 {
		t.Fatalf("corridor room width %d", b.Width)
	}
	// The corridor is closed below its first free row, so the south side has
	// no clear ground beyond a door; the open north end is taken instead.
	if f.Entrance() != North {
		t.Fatalf("entrance %s, expected north", f.Entrance())
	}
	// Deterministic: the same free set yields the same footprint regardless of
	// call order or seed cell within the same region growth start.
	again, _ := GrowFootprint(Cell{5, 10}, free, 49, testRoofSupport)
	if !SameRoomFootprint(f, again) {
		t.Fatal("grow is not deterministic")
	}
	if _, ok := GrowFootprint(Cell{5, 10}, func(c Cell) bool { return free(c) && c.Z <= 6 }, 49, testRoofSupport); ok {
		t.Fatal("room grown where fewer than nine admissible cells exist")
	}
	if _, ok := GrowFootprint(Cell{50, 50}, free, 49, testRoofSupport); ok {
		t.Fatal("room grown from a blocked seed")
	}
}

func TestGrowFootprintConcaveAroundObstacle(t *testing.T) {
	// Free everywhere except a rock pillar; the room must wrap it concavely
	// without placing a wall on rock.
	rock := cellSet([]Cell{{12, 12}, {13, 12}, {12, 13}, {13, 13}})
	free := func(c Cell) bool { return c.X >= 1 && c.Z >= 1 && c.X < 40 && c.Z < 40 && !rock[c] }
	f, ok := GrowFootprint(Cell{10, 12}, free, 40, testRoofSupport)
	if !ok {
		t.Fatal("room not grown around obstacle")
	}
	assertEnclosed(t, f)
	for _, c := range f.Cells() {
		if rock[c] {
			t.Fatalf("shell cell %v on rock", c)
		}
	}
	inside := cellSet(f.Interior())
	perm := rand.New(rand.NewSource(1)).Perm(len(f.Interior()))
	shuffled := make([]Cell, 0, len(perm))
	for _, i := range perm {
		shuffled = append(shuffled, f.Interior()[i])
	}
	rebuilt, err := NewRoomFootprint(shuffled, f.Door(), f.Entrance())
	if err != nil {
		t.Fatal(err)
	}
	if !SameRoomFootprint(f, rebuilt) || len(inside) != len(rebuilt.Interior()) {
		t.Fatal("footprint depends on interior order")
	}
}

func TestUnionFootprintConcaveAndConnector(t *testing.T) {
	// An L: a 7x3 arm along the bottom and a 3x7 arm up the left share their
	// corner; the north-east notch stays outside.
	l, err := UnionFootprint([]InteriorRect{{10, 10, 7, 3}, {10, 10, 3, 7}}, South, testRoofSupport)
	if err != nil {
		t.Fatal(err)
	}
	assertEnclosed(t, l)
	if len(l.Interior()) != 33 || !l.RoofSupported(testRoofSupport) || l.Bounds() != (RoomBounds{9, 9, 9, 9}) {
		t.Fatalf("L interior %d bounds %v", len(l.Interior()), l.Bounds())
	}
	inside := cellSet(l.Interior())
	if inside[Cell{15, 15}] || !inside[Cell{10, 16}] || !inside[Cell{16, 10}] {
		t.Fatal("L interior is not concave around the notch")
	}
	// The door sits on the arm's south wall at the bounding box's centre
	// column, not beside the corner.
	if l.Door() != (Cell{13, 9}) {
		t.Fatalf("L door %v", l.Door())
	}
	// Two 4x4 chambers joined by a one-cell connector: the door opens into a
	// chamber, never into the connector's mouth, on the side nearer the
	// smaller coordinate.
	d, err := UnionFootprint([]InteriorRect{{10, 10, 4, 4}, {14, 12, 3, 1}, {17, 10, 4, 4}}, South, testRoofSupport)
	if err != nil {
		t.Fatal(err)
	}
	assertEnclosed(t, d)
	if len(d.Interior()) != 35 || d.Door() != (Cell{12, 9}) {
		t.Fatalf("connector interior %d door %v", len(d.Interior()), d.Door())
	}
	walls := cellSet(d.Walls())
	for _, c := range []Cell{{14, 11}, {15, 11}, {16, 11}, {14, 13}, {15, 13}, {16, 13}} {
		if !walls[c] {
			t.Fatalf("connector side %v is not walled", c)
		}
	}
	// A north door on the connector room lands on a chamber's north wall.
	n, err := UnionFootprint([]InteriorRect{{10, 10, 4, 4}, {14, 12, 3, 1}, {17, 10, 4, 4}}, North, testRoofSupport)
	if err != nil || n.Door() != (Cell{12, 14}) {
		t.Fatalf("north door %v %v", n.Door(), err)
	}
	// Overlapping parts share cells once; disconnected parts are refused, as
	// are parts against the map edge and an interior beyond roof support.
	same, err := UnionFootprint([]InteriorRect{{10, 10, 5, 5}, {12, 12, 5, 5}, {10, 10, 7, 7}}, East, testRoofSupport)
	if err != nil || len(same.Interior()) != 49 {
		t.Fatal(same, err)
	}
	if _, err := UnionFootprint([]InteriorRect{{10, 10, 3, 3}, {20, 20, 3, 3}}, South, testRoofSupport); err == nil {
		t.Fatal("disconnected parts joined")
	}
	if _, err := UnionFootprint([]InteriorRect{{0, 10, 3, 3}}, South, testRoofSupport); err == nil {
		t.Fatal("part on the map edge accepted")
	}
	if _, err := UnionFootprint([]InteriorRect{{10, 10, 30, 30}}, South, testRoofSupport); err == nil {
		t.Fatal("unsupported roof accepted")
	}
	if _, err := UnionFootprint(nil, South, testRoofSupport); err == nil {
		t.Fatal("empty composite accepted")
	}
}
