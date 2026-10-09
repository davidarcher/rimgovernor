package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The enclosure the wall is traced around: the padded core
// box grown by the yard (perimeterGap, which holds the killbox), closed by
// perimeterThick so the outline has no notches, with its holes filled. The
// ring is every cell within perimeterThick outside it. Field patches and
// geothermal sites are left outside.
type enclosure struct {
	w, h int32
	in   []bool  // the core and its yard
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

// newEnclosure traces the ring around in.
func newEnclosure(in []bool, w, h int32) enclosure {
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

// growRegion is region grown by grow cells (Chebyshev).
func growRegion(region []bool, w, h int32, grow int32) []bool {
	grown := make([]bool, len(region))
	for i, v := range chebyshevField(w, h, region, grow) {
		grown[i] = v >= 0
	}
	return grown
}

// closeRegion clips grown to yard, closes it by perimeterThick and fills its
// holes.
func closeRegion(grown []bool, w, h int32, yard Rectangle) []bool {
	for i := range grown {
		grown[i] = grown[i] && contains(yard, domain.Cell{X: int32(i) % w, Z: int32(i) / w})
	}
	// Closing: dilate, then erode by as much.
	d := chebyshevField(w, h, grown, perimeterThick)
	out := make([]bool, len(grown))
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

// wallInterior is the traced enclosure read back from a plan's
// reservations: the cells the ring closes in, not its bounding
// box. The cover-clear band lies wholly outside the ring, so a flood from
// the rooms that the band and the wall stop is the inside; cells within
// perimeterThick of the band are the ring itself (the opening and terrain
// stretches included) and are left out.
type wallInterior struct {
	box    Rectangle // the ring's bounds grown by the band
	in     []bool    // strictly inside the ring
	closed []bool    // inside or on the ring
}

func (wi wallInterior) at(c domain.Cell) (int, bool) {
	if !contains(wi.box, c) {
		return 0, false
	}
	return int((c.Z-wi.box.Z)*wi.box.Width + c.X - wi.box.X), true
}

// cells lists the inside row by row.
func (wi wallInterior) cells() []domain.Cell {
	var out []domain.Cell
	for i, v := range wi.in {
		if v {
			out = append(out, domain.Cell{X: wi.box.X + int32(i)%wi.box.Width, Z: wi.box.Z + int32(i)/wi.box.Width})
		}
	}
	return out
}

// near is each cell's Chebyshev distance from the ring or its inside, -1
// beyond limit or off the box.
func (wi wallInterior) near(limit int32) func(domain.Cell) int32 {
	d := chebyshevField(wi.box.Width, wi.box.Height, wi.closed, limit)
	return func(c domain.Cell) int32 {
		if i, ok := wi.at(c); ok {
			return d[i]
		}
		return -1
	}
}

// planInterior reads plan's ring back; ok is false for a plan without one.
// grow widens the box past the band for callers measuring out from the
// ring.
func planInterior(plan LayoutPlan, grow int32) (wi wallInterior, ok bool) {
	var ring Rectangle
	wall := map[domain.Cell]bool{}
	var band []Rectangle
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReservePerimeter, ReservePerimeterLight, ReserveBridge, ReservePerimeterGap, ReserveGate:
			ring = unionRect(ring, r.Area)
			for _, c := range rectCells(r.Area) {
				wall[c] = true
			}
		case ReserveCoverClear:
			band = append(band, r.Area)
		}
	}
	if ring.Width == 0 {
		return wi, false
	}
	wi.box = pad(ring, perimeterCoverBand+grow)
	w, h := wi.box.Width, wi.box.Height
	banded := make([]bool, w*h)
	for _, r := range band {
		for _, c := range rectCells(clipRect(r, wi.box)) {
			i, _ := wi.at(c)
			banded[i] = true
		}
	}
	nearBand := chebyshevField(w, h, banded, perimeterThick)
	wi.in, wi.closed = make([]bool, w*h), make([]bool, w*h)
	var q []int32
	seed := func(c domain.Cell) {
		if i, in := wi.at(c); in && !wi.in[i] && !banded[i] && !wall[c] {
			wi.in[i] = true
			q = append(q, int32(i))
		}
	}
	for _, r := range plan.AllRooms() {
		for _, c := range rectCells(r.Interior) {
			seed(c)
		}
	}
	for len(q) > 0 {
		i := q[0]
		q = q[1:]
		c := domain.Cell{X: wi.box.X + i%w, Z: wi.box.Z + i/w}
		for _, n := range [4]domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			seed(n)
		}
	}
	for i := range wi.in {
		if wi.in[i] {
			wi.closed[i] = true
			wi.in[i] = nearBand[i] < 0
		}
	}
	for c := range wall {
		i, _ := wi.at(c)
		wi.closed[i] = true
	}
	return wi, true
}
