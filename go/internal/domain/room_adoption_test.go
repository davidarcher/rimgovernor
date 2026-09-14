package domain

import "testing"

func rectangularAdoption(t *testing.T) RoomAdoption {
	t.Helper()
	r, err := NewRoomAdoption(RoomBounds{X: 10, Z: 10, Width: 5, Height: 5}, South, nil, Cell{}, false)
	if err != nil {
		t.Fatalf("rectangular adoption: %v", err)
	}
	return r
}

func TestRectangularAdoptionDerivesInteriorAndDoor(t *testing.T) {
	r := rectangularAdoption(t)
	if !r.Rectangular() || r.EntranceCellSet() || !r.Set() {
		t.Fatal("rectangular adoption should carry no exact interior or door")
	}
	// A 5 by 5 rectangle has a 3 by 3 interior strictly inside its perimeter.
	if got := len(r.Interior()); got != 9 {
		t.Fatalf("interior cells = %d, want 9", got)
	}
	if r.InteriorCells() != nil {
		t.Fatal("rectangular adoption should report no exact interior")
	}
	// The south door is the midpoint of the low-z edge, the same cell
	// RoomShell.Door places its own door at, so adopting a room this
	// controller built names that room's own door.
	if got, want := r.EntranceCell(), (Cell{X: 12, Z: 10}); got != want {
		t.Fatalf("entrance cell = %v, want %v", got, want)
	}
	shell, err := NewRoomShell(r.Bounds(), "Wall", "Door", "WoodLog", South, ShelterRoom)
	if err != nil {
		t.Fatalf("room shell: %v", err)
	}
	if shell.Door() != r.EntranceCell() {
		t.Fatalf("adoption door %v differs from the shell door %v it would adopt", r.EntranceCell(), shell.Door())
	}
}

func TestAdoptionEntranceCellsPerSide(t *testing.T) {
	bounds := RoomBounds{X: 10, Z: 10, Width: 5, Height: 5}
	for _, row := range []struct {
		side Rotation
		want Cell
	}{{North, Cell{X: 12, Z: 14}}, {South, Cell{X: 12, Z: 10}}, {East, Cell{X: 14, Z: 12}}, {West, Cell{X: 10, Z: 12}}} {
		r, err := NewRoomAdoption(bounds, row.side, nil, Cell{}, false)
		if err != nil {
			t.Fatalf("%s adoption: %v", row.side, err)
		}
		if got := r.EntranceCell(); got != row.want {
			t.Fatalf("%s entrance cell = %v, want %v", row.side, got, row.want)
		}
	}
}

// TestAdoptionRequiresBothNonrectangularFields ports Python's first geometry
// rule: interior cells and entrance cell are supplied together or not at all.
func TestAdoptionRequiresBothNonrectangularFields(t *testing.T) {
	bounds := RoomBounds{X: 0, Z: 0, Width: 5, Height: 5}
	if _, err := NewRoomAdoption(bounds, South, []Cell{{X: 1, Z: 1}}, Cell{}, false); err == nil {
		t.Fatal("interior without an entrance cell should be refused")
	}
	if _, err := NewRoomAdoption(bounds, South, nil, Cell{X: 2, Z: 0}, true); err == nil {
		t.Fatal("entrance cell without an interior should be refused")
	}
}

// interiorRect is every cell of a rectangle, used to build exact interiors.
func interiorRect(x, z, width, height int32) []Cell {
	out := []Cell{}
	for b := z; b < z+height; b++ {
		for a := x; a < x+width; a++ {
			out = append(out, Cell{X: a, Z: b})
		}
	}
	return out
}

func TestNonrectangularAdoptionAccepted(t *testing.T) {
	// A 3 by 3 interior inside a 5 by 5 bound, entered from the south at its
	// own midpoint: the same shape the rectangular form derives, supplied
	// explicitly.
	interior := interiorRect(1, 1, 3, 3)
	r, err := NewRoomAdoption(RoomBounds{X: 0, Z: 0, Width: 5, Height: 5}, South, interior, Cell{X: 2, Z: 0}, true)
	if err != nil {
		t.Fatalf("nonrectangular adoption: %v", err)
	}
	if r.Rectangular() || !r.EntranceCellSet() {
		t.Fatal("nonrectangular adoption should carry an exact interior and door")
	}
	if got := len(r.Interior()); got != 9 {
		t.Fatalf("interior cells = %d, want 9", got)
	}
	if got, want := r.EntranceCell(), (Cell{X: 2, Z: 0}); got != want {
		t.Fatalf("entrance cell = %v, want %v", got, want)
	}
	// The returned slices must not alias the stored interior.
	cells := r.InteriorCells()
	cells[0] = Cell{X: 999, Z: 999}
	if r.InteriorCells()[0] == (Cell{X: 999, Z: 999}) {
		t.Fatal("InteriorCells aliases the stored interior")
	}
}

func TestNonrectangularAdoptionRejections(t *testing.T) {
	bounds := RoomBounds{X: 0, Z: 0, Width: 5, Height: 5}
	door := Cell{X: 2, Z: 0}
	for _, row := range []struct {
		name     string
		interior []Cell
		door     Cell
	}{
		{"duplicate cell", append(interiorRect(1, 1, 3, 3), Cell{X: 1, Z: 1}), door},
		// Strictly inside means the perimeter ring is not interior.
		{"cell on the perimeter", append(interiorRect(1, 1, 3, 3), Cell{X: 0, Z: 2}), door},
		{"cell outside the bounds", append(interiorRect(1, 1, 3, 3), Cell{X: 9, Z: 9}), door},
		// The door itself must not be an interior cell, and neither may the
		// cell beyond it -- an entrance must face outside.
		{"entrance inside the interior", interiorRect(1, 1, 3, 3), Cell{X: 2, Z: 1}},
		// A disconnected interior is two rooms, not one.
		{"disconnected interior", append(interiorRect(1, 1, 1, 1), Cell{X: 3, Z: 3}), Cell{X: 1, Z: 0}},
	} {
		if _, err := NewRoomAdoption(bounds, South, row.interior, row.door, true); err == nil {
			t.Fatalf("%s should be refused", row.name)
		}
	}
}

// TestAdoptionEntranceMustFaceOutside covers Python's (x+dx,z+dz) in points
// rule: a door whose outward neighbour is itself interior is an interior aisle.
func TestAdoptionEntranceMustFaceOutside(t *testing.T) {
	// Entering from the north means the outward step is +z, so a door at z=1
	// with interior at z=2 faces back into the room.
	interior := interiorRect(1, 1, 3, 3)
	if _, err := NewRoomAdoption(RoomBounds{X: 0, Z: 0, Width: 5, Height: 5}, North, interior, Cell{X: 2, Z: 1}, true); err == nil {
		t.Fatal("an entrance facing back into the interior should be refused")
	}
}

func TestAdoptionRejectsInvalidBoundsAndSide(t *testing.T) {
	if _, err := NewRoomAdoption(RoomBounds{X: 0, Z: 0, Width: 3, Height: 5}, South, nil, Cell{}, false); err == nil {
		t.Fatal("a rectangle with no interior should be refused")
	}
	if _, err := NewRoomAdoption(RoomBounds{X: -1, Z: 0, Width: 5, Height: 5}, South, nil, Cell{}, false); err == nil {
		t.Fatal("a negative anchor should be refused")
	}
	if _, err := NewRoomAdoption(RoomBounds{X: 0, Z: 0, Width: 5, Height: 5}, Rotation("up"), nil, Cell{}, false); err == nil {
		t.Fatal("an unsupported entrance side should be refused")
	}
}

func TestRoomAdoptionRoundTrip(t *testing.T) {
	for _, r := range []RoomAdoption{rectangularAdoption(t), func() RoomAdoption {
		r, err := NewRoomAdoption(RoomBounds{X: 0, Z: 0, Width: 5, Height: 5}, South, interiorRect(1, 1, 3, 3), Cell{X: 2, Z: 0}, true)
		if err != nil {
			t.Fatalf("nonrectangular adoption: %v", err)
		}
		return r
	}()} {
		canonical, err := ReconstructRoomAdoption(r)
		if err != nil {
			t.Fatalf("reconstruct: %v", err)
		}
		if !SameRoomAdoption(canonical, r) {
			t.Fatal("adoption did not survive a round trip")
		}
	}
	if SameRoomAdoption(rectangularAdoption(t), RoomAdoption{}) {
		t.Fatal("a zero adoption should not equal a real one")
	}
}

func TestAdoptionEvidenceValidation(t *testing.T) {
	good := AdoptionEvidence{RoomID: "room-1", Cells: 9, Role: "Bedroom", RoleLabel: "bedroom"}
	if err := good.Validate(); err != nil {
		t.Fatalf("valid evidence: %v", err)
	}
	// A role the game did not report is absence, not an error.
	if err := (AdoptionEvidence{RoomID: "room-1", Cells: 1}).Validate(); err != nil {
		t.Fatalf("evidence without a role: %v", err)
	}
	for _, row := range []AdoptionEvidence{
		{RoomID: "", Cells: 9},
		{RoomID: "room-1", Cells: 0},
		{RoomID: "room-1", Cells: 3845},
	} {
		if err := row.Validate(); err == nil {
			t.Fatalf("%#v should be refused", row)
		}
	}
}
