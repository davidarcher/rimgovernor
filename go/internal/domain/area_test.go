package domain

import "testing"

func TestAreaShapes(t *testing.T) {
	cells := []Cell{{X: 3, Z: 1}, {X: 1, Z: 2}}
	for _, bad := range []struct {
		op    AreaOperation
		key   string
		cells []Cell
	}{
		{AreaCreate, "", nil},                           // home is never created
		{AreaDelete, "", nil},                           // nor deleted
		{AreaDelete, "safe", cells},                     // delete takes no cells
		{AreaSetCells, "safe", nil},                     // edits need cells
		{"grow", "safe", cells},                         // unknown operation
		{AreaClearCells, "safe", []Cell{{X: -1, Z: 0}}}, // negative cell
		{AreaSetCells, "", []Cell{{X: 1, Z: 1}, {X: 1, Z: 1}}},
	} {
		if _, err := NewArea(bad.op, bad.key, bad.cells); err == nil {
			t.Fatal("accepted", bad)
		}
	}
	v, err := NewArea(AreaSetCells, "", cells)
	if err != nil || !v.Home() || len(v.Cells()) != len(cells) {
		t.Fatal(v, err)
	}
	a, err := NewAreaAction("a1", v)
	if got, ok := a.Area(); err != nil || !ok || got != v || a.Kind() != AreaAction {
		t.Fatal(a, err)
	}
	if _, err := NewAreaAction("a1", Area{}); err == nil {
		t.Fatal("zero area accepted")
	}
	if c, err := NewArea(AreaCreate, "safe", nil); err != nil || c.Home() || len(c.Cells()) != 0 {
		t.Fatal(c, err)
	}
}

// A block of cells is one rectangle, and the cover expands back to the cells.
func TestAreaRectsCoverCells(t *testing.T) {
	var cells []Cell
	for x := int32(0); x < 200; x++ {
		for z := int32(0); z < 100; z++ {
			if x < 10 || x >= 40 || z < 20 || z >= 60 {
				cells = append(cells, Cell{X: x, Z: z})
			}
		}
	}
	v, err := NewArea(AreaCreate, "wild", cells)
	if err != nil {
		t.Fatal(err)
	}
	if n := len(v.Rects()); n > 4 {
		t.Fatal("a map minus one hole is at most four rectangles, got", n)
	}
	if got := v.Cells(); len(got) != len(cells) {
		t.Fatal("expansion lost cells", len(got), len(cells))
	}
}
