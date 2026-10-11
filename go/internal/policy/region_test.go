package policy

import (
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func regionCellSet(cells []domain.Cell) map[domain.Cell]bool {
	m := map[domain.Cell]bool{}
	for _, c := range cells {
		m[c] = true
	}
	return m
}

func TestRectRegionMatchesRectangleHelpers(t *testing.T) {
	for _, r := range []Rectangle{{X: 3, Z: 4, Width: 1, Height: 1}, {X: 2, Z: 5, Width: 6, Height: 4}, {X: 1, Z: 1, Width: 3, Height: 7}} {
		g := RectRegion(r)
		if !reflect.DeepEqual(g.Cells(), rectCells(r)) {
			t.Fatalf("Cells %v: %v", r, g.Cells())
		}
		if !g.IsRect() || g.Bounds() != r || g.Area() != int(r.Width*r.Height) || len(g.Runs) != int(r.Height) {
			t.Fatalf("rect facts %v: %+v", r, g)
		}
		w := Rectangle{X: r.X - 1, Z: r.Z - 1, Width: r.Width + 2, Height: r.Height + 2}
		want := regionCellSet(rectCells(w))
		for _, c := range rectCells(r) {
			delete(want, c)
		}
		if !reflect.DeepEqual(regionCellSet(g.Walls().Cells()), want) {
			t.Fatalf("Walls %v", r)
		}
	}
	if (Region{}).IsRect() || !RectRegion(Rectangle{Width: 0, Height: 3}).Empty() {
		t.Fatal("empty region")
	}
}

func TestRegionCanonicalAndJSON(t *testing.T) {
	g := RegionOfRuns([]RowRun{{Z: 2, X: 5, Length: 2}, {Z: 1, X: 3, Length: 2}, {Z: 1, X: 5, Length: 1}, {Z: 1, X: 4, Length: 4}, {Z: 2, X: 9, Length: 0}})
	want := []RowRun{{Z: 1, X: 3, Length: 5}, {Z: 2, X: 5, Length: 2}}
	if !reflect.DeepEqual(g.Runs, want) {
		t.Fatalf("runs %v", g.Runs)
	}
	if again := RegionOf(g.Cells()); !reflect.DeepEqual(again, g) || !reflect.DeepEqual(RegionOfRuns(g.Runs), g) {
		t.Fatalf("not idempotent: %v", again)
	}
	b, err := json.Marshal(g)
	if err != nil {
		t.Fatal(err)
	}
	var back Region
	if err := json.Unmarshal(b, &back); err != nil || !reflect.DeepEqual(back, g) {
		t.Fatalf("json %s %v", b, err)
	}
	if !reflect.DeepEqual(RegionOf([]domain.Cell{{X: 1, Z: 1}, {X: 1, Z: 1}}), RectRegion(Rectangle{X: 1, Z: 1, Width: 1, Height: 1})) {
		t.Fatal("duplicates")
	}
}

func TestRegionShapes(t *testing.T) {
	// L: a 3x3 block without its upper-right 2x2.
	l := RegionOf([]domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}, {X: 2, Z: 0}, {X: 0, Z: 1}, {X: 0, Z: 2}})
	if l.IsRect() || l.Area() != 5 || l.Bounds() != (Rectangle{Width: 3, Height: 3}) {
		t.Fatalf("L %+v", l)
	}
	if !l.Contains(domain.Cell{X: 0, Z: 2}) || l.Contains(domain.Cell{X: 1, Z: 1}) || l.Contains(domain.Cell{X: 3, Z: 0}) || l.Contains(domain.Cell{X: -1, Z: 0}) {
		t.Fatal("L contains")
	}
	if !l.Walls().Contains(domain.Cell{X: 1, Z: 1}) || l.Walls().Overlaps(l) {
		t.Fatal("L walls fill the notch corner")
	}

	// Ring with a hole: 5x5 minus the centre cell.
	ring := RectRegion(Rectangle{Width: 5, Height: 5}).minus(RegionOf([]domain.Cell{{X: 2, Z: 2}}))
	if ring.Area() != 24 || ring.Contains(domain.Cell{X: 2, Z: 2}) || ring.IsRect() {
		t.Fatalf("ring %+v", ring)
	}
	if !ring.Walls().Contains(domain.Cell{X: 2, Z: 2}) || ring.Walls().Area() != 7*7-25+1 {
		t.Fatalf("ring walls %d", ring.Walls().Area())
	}
	if ring.Perimeter().Area() != 20 {
		t.Fatalf("ring perimeter %d", ring.Perimeter().Area())
	}
	if p := RectRegion(Rectangle{Width: 4, Height: 4}).Perimeter(); p.Area() != 12 || p.Contains(domain.Cell{X: 1, Z: 1}) {
		t.Fatalf("rect perimeter %v", p)
	}

	// Disjoint pair.
	a, b := RectRegion(Rectangle{X: 0, Z: 0, Width: 2, Height: 2}), RectRegion(Rectangle{X: 5, Z: 0, Width: 2, Height: 2})
	pair := RegionOfRuns(append(append([]RowRun{}, a.Runs...), b.Runs...))
	if pair.Area() != 8 || pair.IsRect() || pair.Contains(domain.Cell{X: 3, Z: 0}) || !pair.Contains(domain.Cell{X: 6, Z: 1}) {
		t.Fatalf("pair %+v", pair)
	}
	if a.Overlaps(b) || !pair.Overlaps(b) || !a.Overlaps(RectRegion(Rectangle{X: 1, Z: 1, Width: 3, Height: 3})) || a.Overlaps(Region{}) {
		t.Fatal("overlaps")
	}
	if a.Overlaps(RectRegion(Rectangle{X: 2, Z: 0, Width: 2, Height: 2})) {
		t.Fatal("touching regions do not overlap")
	}
	if tr := a.Translate(3, -1); tr.Bounds() != (Rectangle{X: 3, Z: -1, Width: 2, Height: 2}) || !reflect.DeepEqual(tr.Translate(-3, 1), a) {
		t.Fatalf("translate %+v", tr)
	}
}
