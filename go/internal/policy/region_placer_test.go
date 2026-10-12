package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func regionOfRows(rows ...string) Region {
	var cells []domain.Cell
	for z, row := range rows {
		for x, ch := range row {
			if ch == '#' {
				cells = append(cells, domain.Cell{X: int32(x), Z: int32(z)})
			}
		}
	}
	return RegionOf(cells)
}

func benchWants(n int) []RegionWant {
	front := domain.Cell{X: 0, Z: -1}
	return []RegionWant{{Slot: "bench", Def: "Bench", Size: domain.Cell{X: 2, Z: 1}, Interaction: &front, Count: n, Min: 1, AgainstWall: true}}
}

func TestPlaceInRegionShapes(t *testing.T) {
	cases := map[string]struct {
		region Region
		door   domain.Cell
		inside domain.Cell
	}{
		"rect":  {regionOfRows("######", "######", "######", "######"), domain.Cell{X: 2, Z: -1}, domain.Cell{X: 2, Z: 0}},
		"L":     {regionOfRows("####..", "####..", "######", "######"), domain.Cell{X: 1, Z: -1}, domain.Cell{X: 1, Z: 0}},
		"T":     {regionOfRows("######", "######", "..##..", "..##.."), domain.Cell{X: 0, Z: -1}, domain.Cell{X: 0, Z: 0}},
		"holed": {regionOfRows("######", "##..##", "##..##", "######"), domain.Cell{X: 0, Z: -1}, domain.Cell{X: 0, Z: 0}},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			req := RegionPlacement{Region: c.region, Doors: []domain.Cell{c.door}, Wants: benchWants(4)}
			pieces, err := PlaceInRegion(req)
			if err != nil || len(pieces) == 0 {
				t.Fatalf("pieces %d err %v", len(pieces), err)
			}
			again, _ := PlaceInRegion(req)
			if !reflect.DeepEqual(pieces, again) {
				t.Fatal("not deterministic")
			}
			blocked := map[domain.Cell]bool{}
			for _, p := range pieces {
				for _, cell := range rectCells(p.Rect) {
					if !c.region.Contains(cell) || cell == c.inside || blocked[cell] {
						t.Fatalf("%s bad cell %v", p.Slot, cell)
					}
					blocked[cell] = true
				}
			}
			reach := regionReach(c.region, blocked, []domain.Cell{c.inside})
			if len(reach) == 0 {
				t.Fatal("door threshold cut off")
			}
			for _, p := range pieces {
				if in, ok := p.Interaction(); !ok || !reach[in] {
					t.Fatalf("%s interaction %v unreachable", p.Slot, in)
				}
			}
		})
	}
}

func TestPlaceInRegionRectValidates(t *testing.T) {
	g := RectRegion(Rectangle{X: 0, Z: 0, Width: 6, Height: 4})
	pieces, err := PlaceInRegion(RegionPlacement{Region: g, Doors: []domain.Cell{{X: 2, Z: -1}}, Wants: benchWants(3)})
	if err != nil || len(pieces) != 3 {
		t.Fatalf("pieces %d err %v", len(pieces), err)
	}
	if s := ScoreInterior(InteriorObjective{Region: g, Doors: []domain.Cell{{X: 2, Z: -1}}, Pieces: pieces, Shapes: testShapes}); !s.Feasible() {
		t.Fatal(s.Violations)
	}
}

func TestPlaceInRegionMinUnmet(t *testing.T) {
	g := RectRegion(Rectangle{X: 0, Z: 0, Width: 2, Height: 1})
	if _, err := PlaceInRegion(RegionPlacement{Region: g, Doors: []domain.Cell{{X: 0, Z: -1}}, Wants: benchWants(1)}); err == nil {
		t.Fatal("a bench with no room for its worker must not place")
	}
}
