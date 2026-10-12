package domain

import (
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

func TestNewRoomFootprintExtraDoors(t *testing.T) {
	bounds := RoomBounds{X: 3, Z: 5, Width: 9, Height: 7}
	one, err := RectangleFootprint(bounds, South)
	if err != nil {
		t.Fatal(err)
	}
	north := RoomDoor{Cell: Cell{X: 7, Z: 11}, Entrance: North}
	two, err := NewRoomFootprint(one.Interior(), one.Door(), South, north)
	if err != nil {
		t.Fatal(err)
	}
	if len(two.Placements("Wall", "Door", "WoodLog")) != len(one.Placements("Wall", "Door", "WoodLog")) || SameRoomFootprint(one, two) || len(two.Doors()) != 2 || len(one.ExtraDoors()) != 0 {
		t.Fatal("two-door footprint")
	}
	if _, err := NewRoomFootprint(one.Interior(), one.Door(), South, RoomDoor{Cell: one.Door(), Entrance: South}); err == nil {
		t.Fatal("duplicate door accepted")
	}
	if _, err := NewRoomFootprint(one.Interior(), one.Door(), South, RoomDoor{Cell: Cell{X: 7, Z: 8}, Entrance: North}); err == nil {
		t.Fatal("interior door accepted")
	}
}
