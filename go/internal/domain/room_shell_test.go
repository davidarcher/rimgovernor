package domain

import "testing"

func shell(t *testing.T, bounds RoomBounds, entrance Rotation) RoomShell {
	t.Helper()
	room, err := NewRoomShell(bounds, "Wall", "Door", "WoodLog", entrance, ShelterRoom)
	if err != nil {
		t.Fatal(err)
	}
	return room
}

// The door cell is the port of Python spatial.room_entrance: the midpoint of
// the named side, with the same integer division.
func TestRoomShellEntranceMatchesPythonMidpoints(t *testing.T) {
	t.Parallel()
	bounds := RoomBounds{X: 10, Z: 20, Width: 7, Height: 6}
	for _, c := range []struct {
		entrance Rotation
		want     Cell
	}{
		{North, Cell{X: 13, Z: 25}},
		{South, Cell{X: 13, Z: 20}},
		{East, Cell{X: 16, Z: 23}},
		{West, Cell{X: 10, Z: 23}},
	} {
		if got := shell(t, bounds, c.entrance).Door(); got != c.want {
			t.Fatal(c.entrance, got, c.want)
		}
	}
}

// room_placements returns the whole perimeter and nothing else: the door
// first, carrying the door definition and the entrance rotation, then every
// remaining perimeter cell as a north-facing wall. Interior cells never
// appear -- the algorithm builds a shell, not a floor.
func TestRoomShellPlacementsCoverPerimeterOnly(t *testing.T) {
	t.Parallel()
	bounds := RoomBounds{X: 3, Z: 4, Width: 5, Height: 6}
	room := shell(t, bounds, South)
	placements := room.Placements()
	if len(placements) != 2*5+2*(6-2) {
		t.Fatal("unexpected placement count", len(placements))
	}
	if first := placements[0]; first.Cell() != room.Door() || first.Definition() != "Door" || first.Rotation() != South {
		t.Fatal("entrance is not the first placement", first)
	}
	seen := map[Cell]bool{}
	for i, p := range placements {
		cell := p.Cell()
		if seen[cell] {
			t.Fatal("duplicate placement", cell)
		}
		seen[cell] = true
		if p.Stuff() != "WoodLog" {
			t.Fatal("material not carried to placement", p)
		}
		perimeter := cell.X == bounds.X || cell.X == bounds.X+bounds.Width-1 || cell.Z == bounds.Z || cell.Z == bounds.Z+bounds.Height-1
		if !perimeter {
			t.Fatal("interior cell placed", cell)
		}
		if i > 0 && (p.Definition() != "Wall" || p.Rotation() != North) {
			t.Fatal("non-entrance placement is not a north wall", p)
		}
	}
	for x := bounds.X; x < bounds.X+bounds.Width; x++ {
		for z := bounds.Z; z < bounds.Z+bounds.Height; z++ {
			cell := Cell{X: x, Z: z}
			perimeter := x == bounds.X || x == bounds.X+bounds.Width-1 || z == bounds.Z || z == bounds.Z+bounds.Height-1
			if perimeter != seen[cell] {
				t.Fatal("perimeter coverage differs", cell, perimeter, seen[cell])
			}
		}
	}
}

// The remaining perimeter follows Python RoomBounds.cells() order, z outer and
// x inner, which is what its stable door-first sort leaves behind.
func TestRoomShellPlacementOrderMatchesPython(t *testing.T) {
	t.Parallel()
	room := shell(t, RoomBounds{X: 0, Z: 0, Width: 4, Height: 4}, West)
	want := []Cell{{X: 0, Z: 2}, {X: 0, Z: 0}, {X: 1, Z: 0}, {X: 2, Z: 0}, {X: 3, Z: 0},
		{X: 0, Z: 1}, {X: 3, Z: 1}, {X: 3, Z: 2}, {X: 0, Z: 3}, {X: 1, Z: 3}, {X: 2, Z: 3}, {X: 3, Z: 3}}
	placements := room.Placements()
	if len(placements) != len(want) {
		t.Fatal(len(placements), len(want))
	}
	for i, cell := range want {
		if placements[i].Cell() != cell {
			t.Fatal("order differs at", i, placements[i].Cell(), cell)
		}
	}
}

// The largest supported rectangle must still fit the 256-action bound a
// committed plan may hold.
func TestRoomShellLargestExpansionFitsPlanBound(t *testing.T) {
	t.Parallel()
	if n := len(shell(t, RoomBounds{X: 0, Z: 0, Width: 64, Height: 64}, North).Placements()); n != 252 || n > 256 {
		t.Fatal("largest expansion", n)
	}
}

func TestRoomShellValidationRejected(t *testing.T) {
	t.Parallel()
	for _, c := range []struct {
		name                 string
		bounds               RoomBounds
		wall, door, material string
		entrance             Rotation
		purpose              RoomPurpose
	}{
		{"no interior", RoomBounds{Width: 3, Height: 5}, "Wall", "Door", "", South, ShelterRoom},
		{"oversized", RoomBounds{Width: 65, Height: 5}, "Wall", "Door", "", South, ShelterRoom},
		{"negative anchor", RoomBounds{X: -1, Width: 4, Height: 4}, "Wall", "Door", "", South, ShelterRoom},
		{"perimeter of doors", RoomBounds{Width: 4, Height: 4}, "Door", "Door", "", South, ShelterRoom},
		{"empty wall", RoomBounds{Width: 4, Height: 4}, "", "Door", "", South, ShelterRoom},
		{"empty door", RoomBounds{Width: 4, Height: 4}, "Wall", "", "", South, ShelterRoom},
		{"bad entrance", RoomBounds{Width: 4, Height: 4}, "Wall", "Door", "", "up", ShelterRoom},
		{"bad purpose", RoomBounds{Width: 4, Height: 4}, "Wall", "Door", "", South, "fortress"},
	} {
		if _, err := NewRoomShell(c.bounds, c.wall, c.door, c.material, c.entrance, c.purpose); err == nil {
			t.Fatal("accepted", c.name)
		}
	}
	// An empty material is the native default request, exactly as Building.Stuff is.
	room, err := NewRoomShell(RoomBounds{Width: 4, Height: 4}, "Wall", "Door", "", South, StorageRoom)
	if err != nil || room.Material() != "" || len(room.Placements()) != 12 {
		t.Fatal(room, err)
	}
	if (RoomShell{}).Set() || len((RoomShell{}).Placements()) != 0 {
		t.Fatal("zero shell is not absent")
	}
	canonical, err := ReconstructRoomShell(room)
	if err != nil || canonical != room {
		t.Fatal(canonical, err)
	}
}

func TestValidateRoomIntent(t *testing.T) {
	t.Parallel()
	for _, id := range []string{"a", "Shelter_01", "north-barracks", "0123456789012345678901234567890123456789"} {
		if err := ValidateRoomIntent(id); err != nil {
			t.Fatal(id, err)
		}
	}
	for _, id := range []string{"", "01234567890123456789012345678901234567890", "has space", "dot.dot", "emoji☃", "null\x00"} {
		if err := ValidateRoomIntent(id); err == nil {
			t.Fatal("accepted", id)
		}
	}
}
