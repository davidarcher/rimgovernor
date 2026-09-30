package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The enclosure the wall is traced around (#1286). It is a region, not a
// rectangle: the padded core box and every fertile patch taken in, whole,
// grown by the yard (perimeterGap), closed by perimeterThick so the outline
// has no notches, with its holes filled. The ring is every cell within
// perimeterThick outside it. A field patch is never crossed: one within
// perimeterFieldReach of the core is taken in, and so is any other the ring
// would touch or come within a cell of. A geothermal enclosure the ring
// would reach is taken in too (#834).
type enclosure struct {
	w, h int32
	in   []bool  // the yard, core and enclosed patches
	ring []bool  // within perimeterThick outside in
	dist []int32 // Chebyshev distance from in, up to the cover band
	bbox Rectangle
}

func (e enclosure) at(c domain.Cell) (int, bool) {
	if c.X < 0 || c.Z < 0 || c.X >= e.w || c.Z >= e.h {
		return 0, false
	}
	return int(c.Z*e.w + c.X), true
}

func (e enclosure) inside(c domain.Cell) bool {
	i, ok := e.at(c)
	return ok && e.in[i]
}

func (e enclosure) onRing(c domain.Cell) bool {
	i, ok := e.at(c)
	return ok && e.ring[i]
}

// ringCells lists the ring row by row.
func (e enclosure) ringCells() []domain.Cell {
	var out []domain.Cell
	for i, r := range e.ring {
		if r {
			out = append(out, domain.Cell{X: int32(i) % e.w, Z: int32(i) / e.w})
		}
	}
	return out
}

// chebyshevField is each cell's Chebyshev distance from src, -1 beyond limit.
func chebyshevField(w, h int32, src []bool, limit int32) []int32 {
	d := make([]int32, len(src))
	var q []int32
	for i, s := range src {
		if s {
			q = append(q, int32(i))
		} else {
			d[i] = -1
		}
	}
	for len(q) > 0 {
		i := q[0]
		q = q[1:]
		if d[i] >= limit {
			continue
		}
		x, z := i%w, i/w
		for dz := int32(-1); dz <= 1; dz++ {
			for dx := int32(-1); dx <= 1; dx++ {
				nx, nz := x+dx, z+dz
				if nx < 0 || nz < 0 || nx >= w || nz >= h {
					continue
				}
				if j := nz*w + nx; d[j] < 0 {
					d[j] = d[i] + 1
					q = append(q, j)
				}
			}
		}
	}
	return d
}

// planEnclosure traces the enclosure around core for plan's field patches
// and geothermal sites on a w x h map.
func planEnclosure(plan LayoutPlan, core Rectangle, w, h int32) enclosure {
	region := make([]bool, w*h)
	mark := func(c domain.Cell) {
		if c.X >= 0 && c.Z >= 0 && c.X < w && c.Z < h {
			region[c.Z*w+c.X] = true
		}
	}
	for _, c := range rectCells(core) {
		mark(c)
	}
	type unit struct {
		cells []domain.Cell
		clear int32 // taken in when within this of the yard
		taken bool
	}
	var units []*unit
	reach := pad(core, perimeterFieldReach)
	for _, z := range plan.Zones {
		if z.Kind != ZoneField {
			continue
		}
		u := &unit{clear: perimeterThick + 1}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				c := domain.Cell{X: x, Z: r.Z}
				u.cells = append(u.cells, c)
				u.taken = u.taken || contains(reach, c)
			}
		}
		units = append(units, u)
	}
	for _, r := range plan.Reservations {
		if r.Kind == ReserveGeothermal {
			units = append(units, &unit{cells: rectCells(r.Area), clear: perimeterThick})
		}
	}
	for _, u := range units {
		if u.taken {
			for _, c := range u.cells {
				mark(c)
			}
		}
	}
	m := LayoutEdgeMargin + perimeterThick
	yard := Rectangle{X: m, Z: m, Width: w - 2*m, Height: h - 2*m}
	for {
		in := encloseRegion(region, w, h, yard)
		d := chebyshevField(w, h, in, perimeterThick+1)
		grew := false
		for _, u := range units {
			if u.taken {
				continue
			}
			for _, c := range u.cells {
				if c.X < 0 || c.Z < 0 || c.X >= w || c.Z >= h {
					continue
				}
				if v := d[c.Z*w+c.X]; v >= 0 && v <= u.clear {
					u.taken, grew = true, true
					break
				}
			}
			if u.taken {
				for _, c := range u.cells {
					mark(c)
				}
			}
		}
		if grew {
			continue
		}
		e := enclosure{w: w, h: h, in: in, ring: make([]bool, w*h), dist: chebyshevField(w, h, in, perimeterThick+perimeterCoverBand)}
		for i, v := range e.dist {
			x, z := int32(i)%w, int32(i)/w
			if in[i] {
				e.bbox = unionRect(e.bbox, Rectangle{X: x, Z: z, Width: 1, Height: 1})
			}
			e.ring[i] = v >= 1 && v <= perimeterThick
		}
		return e
	}
}

// encloseRegion grows region by the yard within yard's bounds, closes it by
// perimeterThick and fills its holes.
func encloseRegion(region []bool, w, h int32, yard Rectangle) []bool {
	d := chebyshevField(w, h, region, perimeterGap)
	grown := make([]bool, len(region))
	for i, v := range d {
		grown[i] = v >= 0 && contains(yard, domain.Cell{X: int32(i) % w, Z: int32(i) / w})
	}
	// Closing: dilate, then erode by as much.
	d = chebyshevField(w, h, grown, perimeterThick)
	out := make([]bool, len(region))
	for i, v := range d {
		out[i] = v < 0
	}
	d = chebyshevField(w, h, out, perimeterThick)
	for i, v := range d {
		out[i] = v < 0 && contains(yard, domain.Cell{X: int32(i) % w, Z: int32(i) / w})
	}
	// Holes: whatever the map edge cannot reach around it.
	seen := make([]bool, len(out))
	var q []int32
	for i := range out {
		x, z := int32(i)%w, int32(i)/w
		if !out[i] && (x == 0 || z == 0 || x == w-1 || z == h-1) {
			seen[i] = true
			q = append(q, int32(i))
		}
	}
	for len(q) > 0 {
		i := q[0]
		q = q[1:]
		x, z := i%w, i/w
		for _, n := range [4][2]int32{{x + 1, z}, {x - 1, z}, {x, z + 1}, {x, z - 1}} {
			if n[0] < 0 || n[1] < 0 || n[0] >= w || n[1] >= h {
				continue
			}
			if j := n[1]*w + n[0]; !out[j] && !seen[j] {
				seen[j] = true
				q = append(q, j)
			}
		}
	}
	for i := range out {
		out[i] = out[i] || !seen[i]
	}
	return out
}

// sides cuts the ring into straight sides, one per straight stretch of the
// enclosure's outline: south and north sides run on past a convex corner
// to own it, as on a rectangle. Each ring cell belongs to at most one side
// position (a full perimeterThick column); a position whose column is not
// wholly ring, or is partly another side's, is left out and its cells are
// walled one by one.
func (e enclosure) sides() (out []ringSide, owner map[domain.Cell]crossing) {
	var raw []ringSide
	type dir struct {
		out      domain.Cell
		vertical bool
	}
	for _, d := range []dir{{domain.Cell{Z: -1}, false}, {domain.Cell{Z: 1}, false}, {domain.Cell{X: 1}, true}, {domain.Cell{X: -1}, true}} {
		in := domain.Cell{X: -d.out.X, Z: -d.out.Z}
		al := domain.Cell{X: 1}
		if d.vertical {
			al = domain.Cell{Z: 1}
		}
		// Rows (or columns) across, positions along.
		across, along := e.h, e.w
		if d.vertical {
			across, along = e.w, e.h
		}
		cell := func(a, p int32) domain.Cell {
			if d.vertical {
				return domain.Cell{X: a, Z: p}
			}
			return domain.Cell{X: p, Z: a}
		}
		edge := func(a, p int32) bool {
			c := cell(a, p)
			return e.inside(c) && !e.inside(addCell(c, d.out))
		}
		for a := int32(0); a < across; a++ {
			for p := int32(0); p < along; p++ {
				if !edge(a, p) {
					continue
				}
				lo := p
				for p+1 < along && edge(a, p+1) {
					p++
				}
				hi := p
				if !d.vertical {
					if !e.inside(cell(a, lo-1)) {
						lo -= perimeterThick
					}
					if !e.inside(cell(a, hi+1)) {
						hi += perimeterThick
					}
				}
				face := a + perimeterThick*(d.out.X+d.out.Z)
				raw = append(raw, ringSide{in: in, al: al, lo: lo, hi: hi, face: face, vertical: d.vertical})
			}
		}
	}
	owned := map[domain.Cell]bool{}
	owner = map[domain.Cell]crossing{}
	for _, sd := range raw {
		start := int32(-1)
		flush := func(end int32) {
			if start >= 0 {
				k := len(out)
				s := sd
				s.lo, s.hi = start, end
				out = append(out, s)
				for p := start; p <= end; p++ {
					for t := int32(0); t < perimeterThick; t++ {
						owner[s.cell(p, t)] = crossing{k, p}
					}
				}
			}
			start = -1
		}
		for p := sd.lo; p <= sd.hi; p++ {
			full := true
			for t := int32(0); t < perimeterThick; t++ {
				c := sd.cell(p, t)
				full = full && e.onRing(c) && !owned[c]
			}
			if !full {
				flush(p - 1)
				continue
			}
			for t := int32(0); t < perimeterThick; t++ {
				owned[sd.cell(p, t)] = true
			}
			if start < 0 {
				start = p
			}
		}
		flush(sd.hi)
	}
	return out, owner
}

// crossing is a side position: a column across the ring.
type crossing struct {
	side int
	pos  int32
}

// cellRects covers cells with rectangles: row runs, stacked runs of the
// same span merged.
func cellRects(set map[domain.Cell]bool) []Rectangle {
	cs := make([]domain.Cell, 0, len(set))
	for c := range set {
		cs = append(cs, c)
	}
	var rects []Rectangle
	open := map[[2]int32]int{} // span -> rect index ending on the last row
	for _, r := range cellRuns(cs) {
		key := [2]int32{r.X, r.Length}
		if i, ok := open[key]; ok && rects[i].Z+rects[i].Height == r.Z {
			rects[i].Height++
			continue
		}
		open[key] = len(rects)
		rects = append(rects, Rectangle{X: r.X, Z: r.Z, Width: r.Length, Height: 1})
	}
	return rects
}
