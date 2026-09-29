package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rectGrid(x0, z0, w, h int32) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for z := z0; z < z0+h; z++ {
		for x := x0; x < x0+w; x++ {
			out[domain.Cell{X: x, Z: z}] = true
		}
	}
	return out
}

func TestPickRectFillsRowsNearestAnchor(t *testing.T) {
	set := rectGrid(0, 0, 10, 10)
	got := PickRect(set, domain.Cell{X: 5, Z: -20}, 9)
	want := rectSorted(rectGrid(4, 0, 3, 3))
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
}

func TestPickRectRespectsHoles(t *testing.T) {
	set := rectGrid(0, 0, 6, 6)
	hole := domain.Cell{X: 1, Z: 1}
	delete(set, hole)
	got := PickRect(set, domain.Cell{X: 0, Z: 0}, 9)
	if len(got) != 9 {
		t.Fatalf("got %d cells", len(got))
	}
	for _, c := range got {
		if !set[c] {
			t.Fatalf("picked %v outside the set", c)
		}
	}
}

func TestPickRectWantOverSetReturnsAll(t *testing.T) {
	set := rectGrid(2, 3, 3, 2)
	got := PickRect(set, domain.Cell{}, 50)
	if !reflect.DeepEqual(got, rectSorted(set)) {
		t.Fatalf("got %v", got)
	}
}

func TestPickRectDeterministicAndNarrowSet(t *testing.T) {
	set := rectGrid(0, 0, 20, 1) // one row: the window must widen
	a := PickRect(set, domain.Cell{X: 10, Z: 0}, 7)
	for i := 0; i < 20; i++ {
		if b := PickRect(set, domain.Cell{X: 10, Z: 0}, 7); !reflect.DeepEqual(a, b) {
			t.Fatalf("nondeterministic: %v vs %v", a, b)
		}
	}
	if len(a) != 7 || a[0].X != 7 || a[6].X != 13 {
		t.Fatalf("got %v", a)
	}
}

func TestFieldBlocksLargestFirst(t *testing.T) {
	p := LayoutPlan{Zones: []LayoutZone{
		{Kind: ZoneField, Runs: []RowRun{{Z: 0, X: 0, Length: 3}}},
		{Kind: ZoneCore, Runs: []RowRun{{Z: 5, X: 0, Length: 30}}},
		{Kind: ZoneField, Runs: []RowRun{{Z: 10, X: 0, Length: 5}, {Z: 11, X: 0, Length: 5}}},
		{Kind: ZoneField, Runs: []RowRun{{Z: 20, X: 0, Length: 3}}},
	}}
	bs := p.FieldBlocks(domain.Cell{X: 0, Z: 21})
	if len(bs) != 3 || len(bs[0]) != 10 || !bs[1][domain.Cell{X: 0, Z: 20}] {
		t.Fatalf("got %v", bs)
	}
}
