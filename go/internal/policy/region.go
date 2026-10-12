package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Region is a cell set stored as row runs, the way LayoutZone stores a zone.
// Runs are canonical: rows ascending, runs within a row ascending by X, no
// empty, overlapping or touching runs. Equal sets therefore compare and
// marshal equal. The zero Region is empty. A rectangle is one run per row.
type Region struct {
	Runs []RowRun
}

// RectRegion is the region of every cell in r; empty when r has no area.
func RectRegion(r Rectangle) Region {
	if r.Width <= 0 || r.Height <= 0 {
		return Region{}
	}
	runs := make([]RowRun, 0, r.Height)
	for z := r.Z; z < r.Z+r.Height; z++ {
		runs = append(runs, RowRun{Z: z, X: r.X, Length: r.Width})
	}
	return Region{Runs: runs}
}

// RegionOf is the region of the given cells; duplicates collapse.
func RegionOf(cells []domain.Cell) Region {
	runs := make([]RowRun, len(cells))
	for i, c := range cells {
		runs[i] = RowRun{Z: c.Z, X: c.X, Length: 1}
	}
	return Region{Runs: canonicalRuns(runs)}
}

// RegionOfRuns canonicalizes arbitrary runs: unsorted, overlapping or touching
// runs merge, empty ones drop.
func RegionOfRuns(runs []RowRun) Region {
	return Region{Runs: canonicalRuns(append([]RowRun(nil), runs...))}
}

func canonicalRuns(runs []RowRun) []RowRun {
	sort.Slice(runs, func(i, j int) bool {
		if runs[i].Z != runs[j].Z {
			return runs[i].Z < runs[j].Z
		}
		return runs[i].X < runs[j].X
	})
	var out []RowRun
	for _, r := range runs {
		if r.Length <= 0 {
			continue
		}
		if n := len(out); n > 0 && out[n-1].Z == r.Z && r.X <= out[n-1].X+out[n-1].Length {
			if end := r.X + r.Length; end > out[n-1].X+out[n-1].Length {
				out[n-1].Length = end - out[n-1].X
			}
			continue
		}
		out = append(out, r)
	}
	return out
}

// Empty reports whether the region has no cells.
func (g Region) Empty() bool { return len(g.Runs) == 0 }

// Area is the number of cells.
func (g Region) Area() int {
	n := 0
	for _, r := range g.Runs {
		n += int(r.Length)
	}
	return n
}

// Bounds is the smallest rectangle holding every cell; zero when empty.
func (g Region) Bounds() Rectangle {
	if g.Empty() {
		return Rectangle{}
	}
	minX, maxX := g.Runs[0].X, g.Runs[0].X+g.Runs[0].Length
	for _, r := range g.Runs {
		minX, maxX = min(minX, r.X), max(maxX, r.X+r.Length)
	}
	minZ, maxZ := g.Runs[0].Z, g.Runs[len(g.Runs)-1].Z
	return Rectangle{X: minX, Z: minZ, Width: maxX - minX, Height: maxZ - minZ + 1}
}

// IsRect reports whether the region fills its bounding rectangle. An empty
// region is not a rectangle.
func (g Region) IsRect() bool {
	b := g.Bounds()
	return !g.Empty() && g.Area() == int(b.Width)*int(b.Height)
}

// Contains reports whether the cell is in the region.
func (g Region) Contains(c domain.Cell) bool {
	i := sort.Search(len(g.Runs), func(i int) bool {
		r := g.Runs[i]
		return r.Z > c.Z || r.Z == c.Z && r.X+r.Length > c.X
	})
	return i < len(g.Runs) && g.Runs[i].Z == c.Z && g.Runs[i].X <= c.X
}

// Cells lists every cell sorted by X then Z, the order of rectCells.
func (g Region) Cells() []domain.Cell {
	if g.Empty() {
		return nil
	}
	out := make([]domain.Cell, 0, g.Area())
	for _, r := range g.Runs {
		for x := r.X; x < r.X+r.Length; x++ {
			out = append(out, domain.Cell{X: x, Z: r.Z})
		}
	}
	sort.Slice(out, func(i, j int) bool { return cellLess(out[i], out[j]) })
	return out
}

// Walls is the ring around the region: every cell outside it that touches it
// orthogonally or diagonally (domain.RoomFootprint's wall rule). For a
// rectangle that is the rectangle grown by one cell, minus the interior.
func (g Region) Walls() Region {
	var runs []RowRun
	for _, r := range g.Runs {
		for dz := int32(-1); dz <= 1; dz++ {
			runs = append(runs, RowRun{Z: r.Z + dz, X: r.X - 1, Length: r.Length + 2})
		}
	}
	return Region{Runs: canonicalRuns(runs)}.minus(g)
}

// Perimeter is the region's own edge: its cells that have an orthogonal
// neighbour outside the region.
func (g Region) Perimeter() Region {
	var out []domain.Cell
	for _, c := range g.Cells() {
		for _, n := range [4]domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if !g.Contains(n) {
				out = append(out, c)
				break
			}
		}
	}
	return RegionOf(out)
}

// Overlaps reports whether the regions share a cell.
func (g Region) Overlaps(o Region) bool {
	i, j := 0, 0
	for i < len(g.Runs) && j < len(o.Runs) {
		a, b := g.Runs[i], o.Runs[j]
		switch {
		case a.Z != b.Z:
			if a.Z < b.Z {
				i++
			} else {
				j++
			}
		case a.X+a.Length <= b.X:
			i++
		case b.X+b.Length <= a.X:
			j++
		default:
			return true
		}
	}
	return false
}

// Translate shifts every cell by (dx, dz).
func (g Region) Translate(dx, dz int32) Region {
	if g.Empty() {
		return Region{}
	}
	runs := make([]RowRun, len(g.Runs))
	for i, r := range g.Runs {
		runs[i] = RowRun{Z: r.Z + dz, X: r.X + dx, Length: r.Length}
	}
	return Region{Runs: runs}
}

// adjacent reports whether c is outside the region and orthogonally next to
// one of its cells: a wall cell a door can stand in, not a diagonal corner.
func (g Region) adjacent(c domain.Cell) bool {
	if g.Contains(c) {
		return false
	}
	for _, n := range [4]domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
		if g.Contains(n) {
			return true
		}
	}
	return false
}

// minus is g without the cells of o. Both are canonical, so the result is.
func (g Region) minus(o Region) Region {
	var runs []RowRun
	for _, r := range g.Runs {
		x := r.X
		for _, s := range o.Runs {
			if s.Z != r.Z || s.X+s.Length <= x || s.X >= r.X+r.Length {
				continue
			}
			if s.X > x {
				runs = append(runs, RowRun{Z: r.Z, X: x, Length: s.X - x})
			}
			x = max(x, s.X+s.Length)
		}
		if x < r.X+r.Length {
			runs = append(runs, RowRun{Z: r.Z, X: x, Length: r.X + r.Length - x})
		}
	}
	return Region{Runs: runs}
}

// Equal reports whether both regions hold the same cells.
func (g Region) Equal(o Region) bool { return slices.Equal(g.Runs, o.Runs) }

// Grow is the region dilated by n cells in every direction (Chebyshev): for a
// rectangle, the rectangle padded by n; for a ring, the ring thickened with its
// hole filled only as far as n reaches. Zero or negative n is the region itself.
func (g Region) Grow(n int32) Region {
	if n <= 0 || g.Empty() {
		return g
	}
	runs := make([]RowRun, 0, len(g.Runs)*int(2*n+1))
	for _, r := range g.Runs {
		for dz := -n; dz <= n; dz++ {
			runs = append(runs, RowRun{Z: r.Z + dz, X: r.X - n, Length: r.Length + 2*n})
		}
	}
	return Region{Runs: canonicalRuns(runs)}
}

// OverlapsRect reports whether the region shares a cell with r. A ring does not
// overlap a rectangle lying wholly in its hole.
func (g Region) OverlapsRect(r Rectangle) bool {
	return g.Overlaps(RectRegion(r))
}
