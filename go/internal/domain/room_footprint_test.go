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
		if !f.RoofSupported() {
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

func TestEllipseFootprintOrientations(t *testing.T) {
	center := Cell{X: 20, Z: 20}
	circle, err := EllipseFootprint(center, 4, 4, EllipseNorthSouth, South)
	if err != nil {
		t.Fatal(err)
	}
	assertEnclosed(t, circle)
	if !circle.RoofSupported() {
		t.Fatal("circle must be roof supported")
	}
	if circle.Door() != (Cell{X: 20, Z: 15}) {
		t.Fatalf("circle door %v", circle.Door())
	}
	for _, o := range []EllipseOrientation{EllipseEastWest, EllipseNorthEast, EllipseNorthWest} {
		same, err := EllipseFootprint(center, 4, 4, o, South)
		if err != nil {
			t.Fatal(err)
		}
		if !SameRoomFootprint(circle, same) {
			t.Fatalf("circle differs under %s", o)
		}
	}
	// Four-fold symmetry of the circle about its centre.
	inside := cellSet(circle.Interior())
	for c := range inside {
		dx, dz := c.X-center.X, c.Z-center.Z
		for _, m := range []Cell{{center.X - dx, center.Z + dz}, {center.X + dx, center.Z - dz}, {center.X + dz, center.Z + dx}} {
			if !inside[m] {
				t.Fatalf("circle asymmetric at %v", m)
			}
		}
	}

	ns, err := EllipseFootprint(center, 3, 5, EllipseNorthSouth, North)
	if err != nil {
		t.Fatal(err)
	}
	ew, err := EllipseFootprint(center, 3, 5, EllipseEastWest, East)
	if err != nil {
		t.Fatal(err)
	}
	if b := ns.Bounds(); b.Width != 2*3+3 || b.Height != 2*5+3 {
		t.Fatalf("north-south bounds %v", b)
	}
	if b := ew.Bounds(); b.Width != 2*5+3 || b.Height != 2*3+3 {
		t.Fatalf("east-west bounds %v", b)
	}
	if ns.Door() != (Cell{X: 20, Z: 26}) || ew.Door() != (Cell{X: 26, Z: 20}) {
		t.Fatalf("doors %v %v", ns.Door(), ew.Door())
	}
	if len(ns.Interior()) != len(ew.Interior()) {
		t.Fatalf("axis swap changed area %d %d", len(ns.Interior()), len(ew.Interior()))
	}
	assertEnclosed(t, ns)
	assertEnclosed(t, ew)

	ne, err := EllipseFootprint(center, 3, 5, EllipseNorthEast, South)
	if err != nil {
		t.Fatal(err)
	}
	nw, err := EllipseFootprint(center, 3, 5, EllipseNorthWest, South)
	if err != nil {
		t.Fatal(err)
	}
	assertEnclosed(t, ne)
	assertEnclosed(t, nw)
	if SameRoomFootprint(ne, nw) || SameRoomFootprint(ne, ns) {
		t.Fatal("diagonal orientations must differ from each other and from axis aligned")
	}
	// The north-east oval's extreme cells lie on the x+z diagonal; mirroring x
	// about the centre gives the north-west oval.
	neSet, nwSet := cellSet(ne.Interior()), cellSet(nw.Interior())
	for c := range neSet {
		if !nwSet[Cell{X: 2*center.X - c.X, Z: c.Z}] {
			t.Fatalf("north-west is not the mirror of north-east at %v", c)
		}
	}
	if !neSet[Cell{X: center.X + 3, Z: center.Z + 3}] || neSet[Cell{X: center.X + 3, Z: center.Z - 3}] {
		t.Fatal("north-east oval must extend along x+z, not x-z")
	}
	for _, f := range []RoomFootprint{ns, ew, ne, nw} {
		if !f.RoofSupported() {
			t.Fatal("small ovals must be roof supported")
		}
		if p := f.Placements("Wall", "Door", "WoodLog"); len(p) != len(f.Walls()) || p[0].Cell() != f.Door() || p[0].Definition() != "Door" {
			t.Fatalf("placements %d walls %d first %v", len(p), len(f.Walls()), p[0])
		}
	}
}

func TestEllipseFootprintBounds(t *testing.T) {
	if _, err := EllipseFootprint(Cell{20, 20}, 1, 4, EllipseNorthSouth, South); err == nil {
		t.Fatal("radius below two accepted")
	}
	if _, err := EllipseFootprint(Cell{20, 20}, 4, 4, EllipseOrientation("tilted"), South); err == nil {
		t.Fatal("unknown orientation accepted")
	}
	if _, err := EllipseFootprint(Cell{40, 40}, 12, 12, EllipseNorthSouth, South); err == nil {
		t.Fatal("interior beyond roof support accepted")
	}
	if _, err := EllipseFootprint(Cell{2, 2}, 4, 4, EllipseNorthSouth, South); err == nil {
		t.Fatal("ellipse off the map edge accepted")
	}
}

func TestGrowFootprintHugsConstrainedTerrain(t *testing.T) {
	// A three-wide corridor of free cells (x 4..6 inclusive is interior room,
	// walls need x 3 and 7) running north from z 3, blocked elsewhere.
	free := func(c Cell) bool { return c.X >= 3 && c.X <= 7 && c.Z >= 3 && c.Z <= 40 }
	f, ok := GrowFootprint(Cell{5, 10}, free, 49)
	if !ok {
		t.Fatal("corridor room not grown")
	}
	assertEnclosed(t, f)
	if len(f.Interior()) != 49 || !f.RoofSupported() {
		t.Fatalf("interior %d supported %v", len(f.Interior()), f.RoofSupported())
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
	again, _ := GrowFootprint(Cell{5, 10}, free, 49)
	if !SameRoomFootprint(f, again) {
		t.Fatal("grow is not deterministic")
	}
	if _, ok := GrowFootprint(Cell{5, 10}, func(c Cell) bool { return free(c) && c.Z <= 6 }, 49); ok {
		t.Fatal("room grown where fewer than nine admissible cells exist")
	}
	if _, ok := GrowFootprint(Cell{50, 50}, free, 49); ok {
		t.Fatal("room grown from a blocked seed")
	}
}

func TestGrowFootprintConcaveAroundObstacle(t *testing.T) {
	// Free everywhere except a rock pillar; the room must wrap it concavely
	// without placing a wall on rock.
	rock := cellSet([]Cell{{12, 12}, {13, 12}, {12, 13}, {13, 13}})
	free := func(c Cell) bool { return c.X >= 1 && c.Z >= 1 && c.X < 40 && c.Z < 40 && !rock[c] }
	f, ok := GrowFootprint(Cell{10, 12}, free, 40)
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
