package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// roofWorld is a 100x100 mirror of known rows; fog deletes a row.
type roofWorld struct {
	rows    map[domain.Cell]SiteCell
	pending map[domain.Cell]bool
	assumed map[domain.Cell]bool
	shrine  map[domain.Cell]bool
}

func newRoofWorld() *roofWorld {
	w := &roofWorld{rows: map[domain.Cell]SiteCell{}, pending: map[domain.Cell]bool{}}
	for z := int32(0); z < 100; z++ {
		for x := int32(0); x < 100; x++ {
			c := domain.Cell{X: x, Z: z}
			w.rows[c] = SiteCell{Cell: c, Roofed: domain.Known(false)}
		}
	}
	return w
}

func (w *roofWorld) roof(x, z int32, on bool) {
	c := domain.Cell{X: x, Z: z}
	r := w.rows[c]
	r.Roofed = domain.Known(on)
	w.rows[c] = r
}

func (w *roofWorld) thing(x, z int32, id uint64, holds bool) {
	c := domain.Cell{X: x, Z: z}
	r := w.rows[c]
	f := FlagEdifice
	if holds {
		f |= FlagHoldsRoof
	}
	r.Things = append(append([]Thing(nil), r.Things...), Thing{Def: "Wall", Category: ThingBuilding, Flags: f, ID: id})
	w.rows[c] = r
}

func (w *roofWorld) holder(x, z int32, on bool) {
	c := domain.Cell{X: x, Z: z}
	r := w.rows[c]
	r.Things = nil
	w.rows[c] = r
	if on {
		w.thing(x, z, 9000+uint64(z)*100+uint64(x), true)
	}
}

func (w *roofWorld) fog(x, z int32) { delete(w.rows, domain.Cell{X: x, Z: z}) }

func (w *roofWorld) grid() RoofSupportGrid {
	return RoofSupportGrid{
		Cell:            func(c domain.Cell) (SiteCell, bool) { r, ok := w.rows[c]; return r, ok },
		Bounds:          Rectangle{X: 1, Z: 1, Width: 98, Height: 98},
		Radius:          2,
		CollapsePending: func(c domain.Cell) bool { return w.pending[c] },
		Structural:      w.shrine,
		AssumedHolders:  w.assumed,
	}
}

func roofCells(xz ...int32) []domain.Cell {
	var out []domain.Cell
	for i := 0; i < len(xz); i += 2 {
		out = append(out, domain.Cell{X: xz[i], Z: xz[i+1]})
	}
	return out
}

// TestRoofBlockerNativeProbe ports native-roof-support/Program.cs.
func TestRoofBlockerNativeProbe(t *testing.T) {
	w := newRoofWorld()
	removed := roofCells(20, 20, 21, 20, 22, 20, 20, 21, 21, 21, 22, 21)
	for _, c := range removed {
		w.holder(c.X, c.Z, true)
	}
	check := func() (string, int) { return w.grid().Blocker(removed) }
	want := func(name string, blocked bool) {
		t.Helper()
		if got, _ := check(); (got != "") != blocked {
			t.Fatalf("%s: blocker %q, blocked want %v", name, got, blocked)
		}
	}
	want("no roof is safe", false)
	w.roof(22, 21, true)
	want("every occupied cell is excluded as a holder", true)
	w.holder(23, 21, true)
	want("adjacent alternate holder supports the roof", false)
	if _, n := check(); n != 1 {
		t.Fatalf("overlapping radial neighbourhoods must count each roof once, got %d", n)
	}
	w.roof(22, 21, false)
	w.roof(24, 21, true)
	w.holder(23, 21, false)
	want("roofs seed from the whole rectangle", true)
	w.holder(25, 21, true)
	want("far-corner roof finds external support", false)
	w.pending[domain.Cell{X: 24, Z: 21}] = true
	if got, _ := check(); got != RoofBlockerCollapse {
		t.Fatalf("pending collapse blocks even with support, got %q", got)
	}
	delete(w.pending, domain.Cell{X: 24, Z: 21})
	saved := w.rows[domain.Cell{X: 24, Z: 20}]
	w.fog(24, 20)
	if got, _ := check(); got != RoofBlockerUnknownGeom {
		t.Fatalf("fog in affected geometry blocks, got %q", got)
	}
	w.rows[domain.Cell{X: 24, Z: 20}] = saved
	// A disconnected roof cannot borrow a holder through unroofed ground.
	w.holder(25, 21, false)
	w.holder(22, 21, true)
	want("excluded holder cannot support a disconnected roof", true)

	// Single-cell wall.
	w = newRoofWorld()
	removed = roofCells(20, 20)
	w.roof(20, 20, true)
	w.holder(20, 20, true)
	w.holder(21, 20, true)
	want("single-cell wall keeps its supported behaviour", false)
	w.holder(21, 20, false)
	want("single-cell wall removal remains blocked", true)

	// Sealed shrine: surveyed fog permits roof checks, not a blanket bypass.
	w.holder(21, 20, true)
	w.fog(20, 20)
	if got, _ := check(); got != RoofBlockerUnknownGeom {
		t.Fatalf("ordinary removal refuses the sealed interior, got %q", got)
	}
	w.shrine = map[domain.Cell]bool{{X: 20, Z: 20}: true}
	want("surveyed sealed roof retains alternate support", false)
	w.holder(21, 20, false)
	want("surveyed fog does not excuse unsupported roof", true)
	w.holder(21, 20, true)
	w.pending[domain.Cell{X: 20, Z: 20}] = true
	want("surveyed fog does not excuse pending collapse", true)
	delete(w.pending, domain.Cell{X: 20, Z: 20})
	w.fog(20, 22)
	want("fog outside the shrine structure still blocks", true)
}

func TestRoofBlockerAssumedHolders(t *testing.T) {
	w := newRoofWorld()
	w.roof(20, 20, true)
	w.holder(20, 20, true)
	g := w.grid()
	if got, _ := g.Blocker(roofCells(20, 20)); got != RoofBlockerUnsupported {
		t.Fatalf("no backup: %q", got)
	}
	w.assumed = map[domain.Cell]bool{{X: 21, Z: 20}: true}
	if got, _ := w.grid().Blocker(roofCells(20, 20)); got != "" {
		t.Fatalf("assumed backup holds the roof: %q", got)
	}
}

// Two buildings each safe alone but unsafe together: the only holders within
// reach of the roof at (20,20) are the wall at (21,20) and a 1x2 pillar pair.
func TestRoofBlockerJoint(t *testing.T) {
	w := newRoofWorld()
	w.roof(20, 20, true)
	w.thing(21, 20, 1, true) // wall A
	w.thing(19, 20, 2, true) // wall B
	g := w.grid()
	a := RemovedBuilding{Cell: domain.Cell{X: 21, Z: 20}, ID: 1}
	b := RemovedBuilding{Cell: domain.Cell{X: 19, Z: 20}, ID: 2}
	for name, batch := range map[string][]RemovedBuilding{"A": {a}, "B": {b}} {
		if got, _ := g.RoofBlocker(batch); got != "" {
			t.Fatalf("%s alone must be safe, got %q", name, got)
		}
	}
	if got, _ := g.RoofBlocker([]RemovedBuilding{a, b}); got != RoofBlockerUnsupported {
		t.Fatalf("together must be unsafe, got %q", got)
	}
}

func TestRoofBlockerFootprintAndNonHolder(t *testing.T) {
	w := newRoofWorld()
	// A 2x2 holder (id 7) listed on each cell, roof over it, one outside holder.
	for _, c := range roofCells(30, 30, 31, 30, 30, 31, 31, 31) {
		w.thing(c.X, c.Z, 7, true)
	}
	w.roof(31, 31, true)
	w.thing(40, 40, 8, false) // a bed-like non-holder
	g := w.grid()
	fp := g.Footprint(domain.Cell{X: 31, Z: 31}, 7)
	if len(fp) != 4 {
		t.Fatalf("footprint %v", fp)
	}
	if got, _ := g.RoofBlocker([]RemovedBuilding{{Cell: domain.Cell{X: 30, Z: 30}, ID: 7}}); got != RoofBlockerUnsupported {
		t.Fatalf("whole footprint is excluded, got %q", got)
	}
	if got, n := g.RoofBlocker([]RemovedBuilding{{Cell: domain.Cell{X: 40, Z: 40}, ID: 8}}); got != "" || n != 0 {
		t.Fatalf("a non-holder removes no support, got %q %d", got, n)
	}
	if got, _ := g.Blocker(nil); got != RoofBlockerNoSupportCells {
		t.Fatalf("empty removal: %q", got)
	}
}
