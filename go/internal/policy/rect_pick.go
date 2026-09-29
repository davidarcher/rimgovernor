package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Rectangle picker (#1222, epic #1212): one picker over a candidate cell
// set (a plan field block, or a room interior) replaces the square patch
// search. It grows a solid run row by row nearest the anchor.

// PickRect returns up to want cells of cells as a rectangle-ish block
// nearest anchor: it seeds at the candidate nearest anchor, takes a column
// window about sqrt(want) wide around the seed, and fills rows outward from
// the seed row (the anchor's side first), skipping cells outside the set.
// A window too narrow for want widens; the last resort is the remaining
// candidates nearest the anchor. want >= len(cells) returns the whole set.
// The result is row-major and deterministic.
func PickRect(cells map[domain.Cell]bool, anchor domain.Cell, want int) []domain.Cell {
	if want <= 0 || len(cells) == 0 {
		return nil
	}
	all := rectSorted(cells)
	if want >= len(all) {
		return all
	}
	seed := all[0]
	for _, c := range all[1:] {
		if rectDist(c, anchor) < rectDist(seed, anchor) {
			seed = c
		}
	}
	minZ, maxZ, minX, maxX := seed.Z, seed.Z, seed.X, seed.X
	for _, c := range all {
		minZ, maxZ = min(minZ, c.Z), max(maxZ, c.Z)
		minX, maxX = min(minX, c.X), max(maxX, c.X)
	}
	// Rows outward from the seed, the side nearer the anchor first.
	var rows []int32
	for d := int32(0); seed.Z-d >= minZ || seed.Z+d <= maxZ; d++ {
		a, b := seed.Z-d, seed.Z+d
		if anchor.Z > seed.Z {
			a, b = b, a
		}
		rows = append(rows, a)
		if d > 0 {
			rows = append(rows, b)
		}
	}
	w := int32(1)
	for int(w*w) < want {
		w++
	}
	var picked map[domain.Cell]bool
	for ; ; w++ {
		picked = rectWindow(cells, seed, anchor, rows, w, want)
		if len(picked) >= want || w > maxX-minX {
			break
		}
	}
	if len(picked) < want {
		rest := make([]domain.Cell, 0, len(all))
		for _, c := range all {
			if !picked[c] {
				rest = append(rest, c)
			}
		}
		sort.SliceStable(rest, func(i, j int) bool { return rectDist(rest[i], anchor) < rectDist(rest[j], anchor) })
		for _, c := range rest[:want-len(picked)] {
			picked[c] = true
		}
	}
	return rectSorted(picked)
}

// rectWindow fills rows in order inside a w-wide column window about the
// seed (leaning to the anchor's side), columns nearest the seed first,
// taking a row only while it touches a row already taken.
func rectWindow(cells map[domain.Cell]bool, seed, anchor domain.Cell, rows []int32, w int32, want int) map[domain.Cell]bool {
	lo := seed.X - w/2
	if anchor.X > seed.X {
		lo = seed.X - (w-1)/2
	}
	cols := make([]int32, 0, w)
	for x := lo; x < lo+w; x++ {
		cols = append(cols, x)
	}
	sort.SliceStable(cols, func(i, j int) bool {
		di, dj := rectAbs(cols[i]-seed.X), rectAbs(cols[j]-seed.X)
		if di != dj {
			return di < dj
		}
		return rectAbs(cols[i]-anchor.X) < rectAbs(cols[j]-anchor.X)
	})
	out := map[domain.Cell]bool{}
	taken := map[int32]bool{}
	for _, z := range rows {
		if len(out) >= want {
			break
		}
		if z != seed.Z && !taken[z-1] && !taken[z+1] {
			continue
		}
		for _, x := range cols {
			c := domain.Cell{X: x, Z: z}
			if cells[c] && len(out) < want {
				out[c] = true
				taken[z] = true
			}
		}
	}
	return out
}

// FieldBlocks returns each plan field zone as its own candidate set for
// PickRect: largest first, then nearest anchor, then by first cell.
func (p LayoutPlan) FieldBlocks(anchor domain.Cell) []map[domain.Cell]bool {
	type block struct {
		cells map[domain.Cell]bool
		near  int64
		first domain.Cell
	}
	var bs []block
	for _, z := range p.Zones {
		if z.Kind != ZoneField || len(z.Runs) == 0 {
			continue
		}
		b := block{cells: map[domain.Cell]bool{}, near: -1}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				c := domain.Cell{X: x, Z: r.Z}
				b.cells[c] = true
				if d := rectDist(c, anchor); b.near < 0 || d < b.near {
					b.near = d
				}
			}
		}
		if len(b.cells) == 0 {
			continue
		}
		b.first = rectSorted(b.cells)[0]
		bs = append(bs, b)
	}
	sort.SliceStable(bs, func(i, j int) bool {
		if len(bs[i].cells) != len(bs[j].cells) {
			return len(bs[i].cells) > len(bs[j].cells)
		}
		if bs[i].near != bs[j].near {
			return bs[i].near < bs[j].near
		}
		if bs[i].first.Z != bs[j].first.Z {
			return bs[i].first.Z < bs[j].first.Z
		}
		return bs[i].first.X < bs[j].first.X
	})
	out := make([]map[domain.Cell]bool, len(bs))
	for i, b := range bs {
		out[i] = b.cells
	}
	return out
}

func rectSorted(set map[domain.Cell]bool) []domain.Cell {
	out := make([]domain.Cell, 0, len(set))
	for c := range set {
		out = append(out, c)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Z != out[j].Z {
			return out[i].Z < out[j].Z
		}
		return out[i].X < out[j].X
	})
	return out
}

func rectDist(a, b domain.Cell) int64 {
	dx, dz := int64(a.X-b.X), int64(a.Z-b.Z)
	return dx*dx + dz*dz
}

func rectAbs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}
