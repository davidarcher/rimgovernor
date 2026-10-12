package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The enclosure price of an unpriced room (#2987). A group the plan links by no
// flow (an outskirts room whose flows are not modelled) has no walk to price
// its distance, so it is not placed by the tie-break of the cell terms alone: it
// is still part of the base, and the base pays the wall around it and the ground
// that wall encloses. blockCost therefore adds, for such a group, the marginal
// Wall and Footprint terms of PlanScore: a room far from the rest needs a ring
// of its own, one beside them shares the rest's, so scattering costs perimeter.
// Both weights are planWeights'; no constant is new.
//
// The terms are read off the rectangles, not the traced ring (coreEnclosure):
// the enclosure of a block is the block with its wall cells grown by the killbox
// yard (perimeterGap), the ring the cells within perimeterThick outside it, and
// a wall is built on the ring cells that are not impassable (PlanPerimeter's
// impassable: rock, hazard, unwalkable ground). The ring of a union is the union
// grown by the thickness less the union (ring(X) = grow(X) \ X), so adding block
// r to the others I costs
//
//	|walkable(grow(r) \ grow(I))| - |walkable(r \ I)|    wall cells
//	|pad(r, 1) \ pad(I, 1)|                              footprint cells
//
// (grow = by perimeterThick). Closing the outline, filling holes and the
// shoreline detours are left to the plan's real score; fields are outside the
// ring.

// unpriced reports a room group none of whose links is a flow (positive
// weight): nothing prices its distance. A required link alone (the incinerator
// beside the waste yard) is a structural rule, not a trip.
func (e *embedder) unpriced(i int) bool {
	if e.isField(i) {
		return false
	}
	for _, l := range e.groups[i].Links {
		if l.Weight > 0 {
			return false
		}
	}
	return true
}

// enclosureSet is the rectangles the other blocks enclose, by X: each block
// grown by the yard, by the yard and the thickness, and by the wall cell. They
// depend on the group and the placed blocks, not the candidate, so a placement
// call builds them once.
type enclosureSet struct {
	in, grown, walls xRects
	scratch          coverScratch
	calls            int
	grids            *[3]coverGrid // grown, in, walls: built after enough calls
}

// xRects is rectangles sorted by X with reach[i] the farthest right edge among
// rects[0..i], so a scan starts at the first rectangle that can touch k.
type xRects struct {
	rects []Rectangle
	reach []int32
}

func newXRects(rects []Rectangle) xRects {
	slices.SortFunc(rects, func(a, b Rectangle) int { return int(a.X - b.X) })
	reach := make([]int32, len(rects))
	var far int32 = math.MinInt32
	for i, r := range rects {
		far = max(far, r.X+r.Width)
		reach[i] = far
	}
	return xRects{rects, reach}
}

// coverScratch is the buffers one uncovered call fills, kept across the calls
// of a placement (one goroutine).
type coverScratch struct {
	clipped []Rectangle
	active  []Rectangle
	xs      []int32
	spans   [][2]int32
}

// enclosureRects is the enclosureSet of group i against the other blocks.
func (e *embedder) enclosureRects(i int, blocks []Rectangle) *enclosureSet {
	const yard = 1 + perimeterGap // the wall cells and the killbox yard
	var in, grown, walls []Rectangle
	for j, b := range blocks {
		if j == i || b.Width == 0 || e.isField(j) {
			continue
		}
		in = append(in, pad(b, yard))
		grown = append(grown, pad(b, yard+perimeterThick))
		walls = append(walls, pad(b, 1))
	}
	return &enclosureSet{in: newXRects(in), grown: newXRects(grown), walls: newXRects(walls)}
}

// enclosureCost is the Wall and Footprint cost of a group at r given the
// enclosureSet of the other blocks.
func (e *embedder) enclosureCost(r Rectangle, s *enclosureSet) float64 {
	const yard = 1 + perimeterGap // the wall cells and the killbox yard
	onMap := Rectangle{Width: e.base.w, Height: e.base.h}
	s.calls++
	if s.grids == nil && s.calls >= max(coverGridMinCalls, int(onMap.Width)*int(onMap.Height)/coverGridCellsPerCall) {
		s.grids = &[3]coverGrid{newCoverGrid(onMap, s.grown, &e.walkable), newCoverGrid(onMap, s.in, &e.walkable), newCoverGrid(onMap, s.walls, nil)}
	}
	if g := s.grids; g != nil {
		wall := g[0].uncovered(clipRect(pad(r, yard+perimeterThick), onMap), &e.walkable) -
			g[1].uncovered(clipRect(pad(r, yard), onMap), &e.walkable)
		return float64(planWeights.Wall*wall + planWeights.Footprint*g[2].uncovered(pad(r, 1), nil))
	}
	wall := s.scratch.uncovered(clipRect(pad(r, yard+perimeterThick), onMap), s.grown, &e.walkable) -
		s.scratch.uncovered(clipRect(pad(r, yard), onMap), s.in, &e.walkable)
	return float64(planWeights.Wall*wall + planWeights.Footprint*s.scratch.uncovered(pad(r, 1), s.walls, nil))
}

// A placement that prices more than coverGridMinCalls candidates, or one a
// coverGridCellsPerCall-th of the map's cells, reads them off coverGrids built
// once: the grids cost a few passes over the map, a sweep costs per candidate.
const (
	coverGridMinCalls     = 64
	coverGridCellsPerCall = 1024
)

// uncovered is the number of cells of k, weighted by weight (each counts 1 when
// nil), that none of rects covers. k lies on the map. rects are sorted by X.
func uncovered(k Rectangle, rects []Rectangle, weight *prefix) int {
	var sc coverScratch
	return sc.uncovered(k, newXRects(slices.Clone(rects)), weight)
}

func (sc *coverScratch) uncovered(k Rectangle, rects xRects, weight *prefix) int {
	free := func(x0, x1, z0, z1 int32) int {
		if z1 <= z0 {
			return 0
		}
		if weight == nil {
			return int(x1-x0) * int(z1-z0)
		}
		return int(weight.sum(Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}))
	}
	if k.Width <= 0 || k.Height <= 0 {
		return 0
	}
	clipped := sc.clipped[:0]
	xs := append(sc.xs[:0], k.X, k.X+k.Width)
	// Rectangles whose reach does not pass k's left edge cannot touch it.
	first := sort.Search(len(rects.reach), func(i int) bool { return rects.reach[i] > k.X })
	for _, r := range rects.rects[first:] {
		if r.X >= k.X+k.Width {
			break
		}
		if c := clipRect(r, k); c.Width > 0 {
			clipped = append(clipped, c)
			xs = append(xs, c.X, c.X+c.Width)
		}
	}
	slices.Sort(xs)
	xs = slices.Compact(xs)
	total := 0
	spans := sc.spans[:0]
	// clipped is sorted by X and the strips run left to right, so a rectangle
	// covers every strip from its left edge to its right: the active ones are
	// those started (X <= x0) and not yet passed (right edge > x0; every right
	// edge is a strip boundary, so one still active covers the whole strip).
	active := sc.active[:0]
	next := 0
	for s := 0; s+1 < len(xs); s++ {
		x0, x1 := xs[s], xs[s+1]
		for next < len(clipped) && clipped[next].X <= x0 {
			active = append(active, clipped[next])
			next++
		}
		kept := active[:0]
		spans = spans[:0]
		for _, c := range active {
			if c.X+c.Width <= x0 {
				continue
			}
			kept = append(kept, c)
			// Insertion sort by start: a strip holds a handful of spans.
			sp := [2]int32{c.Z, c.Z + c.Height}
			spans = append(spans, sp)
			j := len(spans) - 1
			for ; j > 0 && spans[j-1][0] > sp[0]; j-- {
				spans[j] = spans[j-1]
			}
			spans[j] = sp
		}
		active = kept
		at := k.Z
		for _, sp := range spans {
			total += free(x0, x1, at, min(sp[0], k.Z+k.Height))
			at = max(at, sp[1])
		}
		total += free(x0, x1, at, k.Z+k.Height)
	}
	sc.clipped, sc.xs, sc.spans, sc.active = clipped, xs, spans, active
	return total
}

// coverGrid is the prefix sums of the cells a set of rectangles covers (each
// counting its weight, or 1 unweighted), so a placement that prices many
// candidates against the same blocks reads the covered part of any k in O(1)
// instead of sweeping the rectangles per candidate.
type coverGrid struct {
	bounds Rectangle
	s      []int32 // (Width+1) x (Height+1) prefix sums
}

// newCoverGrid is the grid over the bounding box of onMap and rects. weight
// (nil: each cell counts 1) reads only cells on the map.
func newCoverGrid(onMap Rectangle, rects xRects, weight *prefix) coverGrid {
	x0, z0, x1, z1 := onMap.X, onMap.Z, onMap.X+onMap.Width, onMap.Z+onMap.Height
	for _, r := range rects.rects {
		x0, z0 = min(x0, r.X), min(z0, r.Z)
		x1, z1 = max(x1, r.X+r.Width), max(z1, r.Z+r.Height)
	}
	g := coverGrid{bounds: Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}}
	stride := int(g.bounds.Width) + 1
	g.s = make([]int32, stride*(int(g.bounds.Height)+1))
	for _, r := range rects.rects {
		if r.Width <= 0 || r.Height <= 0 {
			continue
		}
		for z := r.Z; z < r.Z+r.Height; z++ {
			base := int(z-z0+1)*stride + 1 - int(x0)
			for x := r.X; x < r.X+r.Width; x++ {
				g.s[base+int(x)] = 1
			}
		}
	}
	for z := int32(0); z < g.bounds.Height; z++ {
		for x := int32(0); x < g.bounds.Width; x++ {
			i := int(z+1)*stride + int(x+1)
			v := g.s[i]
			if v != 0 && weight != nil {
				v = 0
				if c := (Rectangle{X: x + x0, Z: z + z0, Width: 1, Height: 1}); onMapCell(onMap, c) {
					v = int32(weight.sum(c))
				}
			}
			g.s[i] = v + g.s[i-stride] + g.s[i-1] - g.s[i-stride-1]
		}
	}
	return g
}

func onMapCell(m, c Rectangle) bool {
	return c.X >= m.X && c.Z >= m.Z && c.X < m.X+m.Width && c.Z < m.Z+m.Height
}

// covered is the covered weight inside k.
func (g coverGrid) covered(k Rectangle) int {
	k = clipRect(k, g.bounds)
	if k.Width == 0 {
		return 0
	}
	stride := int(g.bounds.Width) + 1
	x0, z0 := int(k.X-g.bounds.X), int(k.Z-g.bounds.Z)
	x1, z1 := x0+int(k.Width), z0+int(k.Height)
	return int(g.s[z1*stride+x1] - g.s[z0*stride+x1] - g.s[z1*stride+x0] + g.s[z0*stride+x0])
}

// uncovered is sc.uncovered read off the grid: k's own weight less the covered.
func (g coverGrid) uncovered(k Rectangle, weight *prefix) int {
	if k.Width <= 0 || k.Height <= 0 {
		return 0
	}
	if weight == nil {
		return int(k.Width)*int(k.Height) - g.covered(k)
	}
	return int(weight.sum(k)) - g.covered(k)
}

// walkableCell reports a cell a wall can be built on: PlanPerimeter's impassable
// is rock, hazard or ground nothing walks on (an unread cell is not impassable).
func walkableCell(cells map[domain.Cell]SurveyCell, c domain.Cell) bool {
	sc, ok := cells[c]
	return !ok || !(sc.Rock || sc.Hazard || !sc.Walkable && !sc.Built)
}
