package policy

import (
	"slices"

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
type enclosureSet struct{ in, grown, walls []Rectangle }

// enclosureRects is the enclosureSet of group i against the other blocks.
func (e *embedder) enclosureRects(i int, blocks []Rectangle) *enclosureSet {
	const yard = 1 + perimeterGap // the wall cells and the killbox yard
	s := &enclosureSet{}
	for j, b := range blocks {
		if j == i || b.Width == 0 || e.isField(j) {
			continue
		}
		s.in = append(s.in, pad(b, yard))
		s.grown = append(s.grown, pad(b, yard+perimeterThick))
		s.walls = append(s.walls, pad(b, 1))
	}
	byX := func(a, b Rectangle) int { return int(a.X - b.X) }
	slices.SortFunc(s.in, byX)
	slices.SortFunc(s.grown, byX)
	slices.SortFunc(s.walls, byX)
	return s
}

// enclosureCost is the Wall and Footprint cost of a group at r given the
// enclosureSet of the other blocks.
func (e *embedder) enclosureCost(r Rectangle, s *enclosureSet) float64 {
	const yard = 1 + perimeterGap // the wall cells and the killbox yard
	onMap := Rectangle{Width: e.base.w, Height: e.base.h}
	wall := uncovered(clipRect(pad(r, yard+perimeterThick), onMap), s.grown, &e.walkable) -
		uncovered(clipRect(pad(r, yard), onMap), s.in, &e.walkable)
	return float64(planWeights.Wall*wall + planWeights.Footprint*uncovered(pad(r, 1), s.walls, nil))
}

// uncovered is the number of cells of k, weighted by weight (each counts 1 when
// nil), that none of rects covers. k lies on the map. rects are sorted by X, so
// the scan stops at the first one right of k.
func uncovered(k Rectangle, rects []Rectangle, weight *prefix) int {
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
	var clipped []Rectangle
	xs := []int32{k.X, k.X + k.Width}
	for _, r := range rects {
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
	var spans [][2]int32
	for s := 0; s+1 < len(xs); s++ {
		x0, x1 := xs[s], xs[s+1]
		spans = spans[:0]
		for _, c := range clipped {
			if c.X <= x0 && c.X+c.Width >= x1 {
				spans = append(spans, [2]int32{c.Z, c.Z + c.Height})
			}
		}
		slices.SortFunc(spans, func(a, b [2]int32) int { return int(a[0] - b[0]) })
		at := k.Z
		for _, sp := range spans {
			total += free(x0, x1, at, min(sp[0], k.Z+k.Height))
			at = max(at, sp[1])
		}
		total += free(x0, x1, at, k.Z+k.Height)
	}
	return total
}

// walkableCell reports a cell a wall can be built on: PlanPerimeter's impassable
// is rock, hazard or ground nothing walks on (an unread cell is not impassable).
func walkableCell(cells map[domain.Cell]SurveyCell, c domain.Cell) bool {
	sc, ok := cells[c]
	return !ok || !(sc.Rock || sc.Hazard || !sc.Walkable && !sc.Built)
}
