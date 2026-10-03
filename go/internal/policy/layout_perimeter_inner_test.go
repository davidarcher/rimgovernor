package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A cross wall never starts in front of a ring gate: the gate would open
// onto the wall's end.
func TestInnerCrossWallLeavesRingGatesOpen(t *testing.T) {
	core := Rectangle{X: 100, Z: 100, Width: 10, Height: 10}
	bound := pad(core, 40)
	walls, gates := innerWalls(core, func(c domain.Cell) bool { return contains(bound, c) }, func(domain.Cell, bool) bool { return true })
	if len(gates) < 4 {
		t.Fatalf("gates = %d, want the four ring gates first", len(gates))
	}
	outward := []domain.Cell{{X: 0, Z: -1}, {X: 0, Z: 1}, {X: -1, Z: 0}, {X: 1, Z: 0}}
	for i, o := range outward {
		g := gates[i]
		// The cell outside the gate's middle, past the ring's thickness side.
		mid := domain.Cell{X: g.X + g.Width/2, Z: g.Z + g.Height/2}
		out := domain.Cell{X: mid.X + o.X*(max(g.Width, 1)/2+1), Z: mid.Z + o.Z*(max(g.Height, 1)/2+1)}
		if walls[out] {
			t.Errorf("ring gate %d at %+v opens onto wall cell %+v", i, g, out)
		}
	}
}

// A cross wall's gate stands in the wall with open ground at both ends.
func TestInnerCrossWallGatesHaveOpenEnds(t *testing.T) {
	core := Rectangle{X: 100, Z: 100, Width: 10, Height: 10}
	bound := pad(core, 40)
	walls, gates := innerWalls(core, func(c domain.Cell) bool { return contains(bound, c) }, func(domain.Cell, bool) bool { return true })
	for i, g := range gates[4:] {
		for _, c := range rectCells(g) {
			if !walls[c] {
				t.Errorf("cross gate %d at %+v has a cell %+v outside the wall", i, g, c)
			}
		}
		var a, b domain.Cell
		if g.Width > g.Height {
			a, b = domain.Cell{X: g.X - 1, Z: g.Z}, domain.Cell{X: g.X + g.Width, Z: g.Z}
		} else {
			a, b = domain.Cell{X: g.X, Z: g.Z - 1}, domain.Cell{X: g.X, Z: g.Z + g.Height}
		}
		if walls[a] || walls[b] {
			t.Errorf("cross gate %d at %+v opens onto a wall (%v %v)", i, g, walls[a], walls[b])
		}
	}
}
