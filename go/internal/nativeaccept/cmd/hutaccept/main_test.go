package main

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A 3x3 interior ring: every wall is load-bearing except the four corners.
func testShell(t *testing.T) *shell {
	t.Helper()
	var interior []domain.Cell
	for x := int32(1); x <= 3; x++ {
		for z := int32(1); z <= 3; z++ {
			interior = append(interior, domain.Cell{X: x, Z: z})
		}
	}
	f, err := domain.NewRoomFootprint(interior, domain.Cell{X: 2, Z: 0}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	sh := &shell{footprint: f, cells: map[domain.Cell]int{}}
	for i, c := range f.Walls() {
		sh.cells[c] = i
	}
	return sh
}

func TestMissingCellsAreBearingNearestTheDoorAndNeverTheDoor(t *testing.T) {
	sh := testShell(t)
	open := func(domain.Cell) bool { return true }
	got := missingCells(sh, 3, open)
	if len(got) != 3 {
		t.Fatalf("got %v, want three cells", got)
	}
	for _, c := range got {
		if c == sh.footprint.Door() {
			t.Fatalf("%v is the door", got)
		}
		if !sh.bearing(c) {
			t.Fatalf("%v is not load-bearing", c)
		}
		if d := abs(c.X-2) + abs(c.Z-0); d > 3 {
			t.Fatalf("%v is %d from the door; nearer bearing walls exist", c, d)
		}
	}
	if len(missingCells(sh, 100, open)) != len(sh.footprint.Walls())-1-4 {
		t.Fatalf("every bearing wall but the door and the corners should be eligible")
	}
	// Rock beside a wall seals its gap: the cell is left standing.
	rock := domain.Cell{X: -1, Z: 2}
	for _, c := range missingCells(sh, 100, func(c domain.Cell) bool { return c != rock }) {
		if c == (domain.Cell{X: 0, Z: 2}) {
			t.Fatalf("a wall sealed by rock was left to the builders")
		}
	}
}

func TestSameCorridorComparesCentreAndRows(t *testing.T) {
	a := map[string]any{"center": map[string]any{"x": 1.0, "z": 2.0}, "rows": []any{map[string]any{"z": 2.0, "placed": 9.0}}, "tick": 1.0}
	b := map[string]any{"center": map[string]any{"x": 1.0, "z": 2.0}, "rows": []any{map[string]any{"z": 2.0, "placed": 9.0}}, "tick": 7.0}
	c := map[string]any{"center": map[string]any{"x": 1.0, "z": 3.0}, "rows": a["rows"]}
	if !sameCorridor(a, b) || sameCorridor(a, c) || sameCorridor(a, nil) {
		t.Fatal("sameCorridor should compare only centre and rows")
	}
}
