package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RegionWant is one kind of furniture a region should hold: Count pieces of
// Def, at least Min of them. Slot names the pieces ("bench.0", "bench.1").
type RegionWant struct {
	Slot string
	Def  string
	// Size and Interaction are the definition's shape at North.
	Size        domain.Cell
	Interaction *domain.Cell
	Count, Min  int
	// Names, when set, names the pieces in placement order ("bed", "bunk.1")
	// where the slot names are read by a consumer; piece i past the list is
	// "Slot.i". Without it every piece is "Slot.i".
	Names []string
	// AgainstWall requires a footprint cell beside a cell outside the region
	// (a wall, or the edge of a hole). Without it the piece still prefers a
	// wall cell and falls back to the open floor.
	AgainstWall bool
	// Beside names another want (by Slot) whose pieces each piece of this one
	// faces and touches, a chair at its table. It is a link the objective
	// prices (facilityLinks), not a veto: the greedy placer seats a piece
	// beside its partner when it can, and only the pieces up to Min fall back
	// to any free cell; a piece beyond Min with no partner to sit at is left
	// out rather than stood alone.
	Beside string
	// Clear keeps that many cells open in a straight line in front of the
	// piece (where its user stands, behind its interaction cell), a horseshoes
	// pin's throw lane.
	Clear int32
	// Open seats the piece where the most of the pieces that sit beside it
	// (the wants whose Beside is this Slot) still fit, nearest the doors
	// first, before any wall preference: a table with room for its chairs.
	Open bool
}

// seatsAround is how many of the pieces that sit beside p the floor around it
// then holds, placed greedily on the cells next to it.
func seatsAround(g Region, blocked, keep map[domain.Cell]bool, thresholds []domain.Cell, wants []RegionWant, p InteriorPiece) int {
	var dep *RegionWant
	for i := range wants {
		if wants[i].Beside == p.Row {
			dep = &wants[i]
			break
		}
	}
	if dep == nil {
		return 0
	}
	b, k := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for c := range blocked {
		b[c] = true
	}
	for c := range keep {
		k[c] = true
	}
	var ring []domain.Cell
	for _, c := range rectCells(p.Rect) {
		b[c] = true
		for _, n := range neighbours(c) {
			if g.Contains(n) && !rectContains(p.Rect, n) && !slices.Contains(ring, n) {
				ring = append(ring, n)
			}
		}
	}
	for _, c := range p.Reserves() {
		k[c] = true
	}
	placed := []InteriorPiece{p}
	seats := 0
	for seats < dep.Count {
		s, ok := nextRegionPiece(g, ring, b, k, thresholds, placed, nil, *dep, dep.slotName(seats), false)
		if !ok {
			break
		}
		for _, c := range rectCells(s.Rect) {
			b[c] = true
		}
		placed = append(placed, s)
		seats++
	}
	return seats
}

// openRegionPiece is the want's Open piece: the fitting stand that seats the
// most pieces beside it, the nearest a door, then the first rotation and
// corner.
func openRegionPiece(g Region, cells []domain.Cell, blocked, keep, before map[domain.Cell]bool, thresholds []domain.Cell, wants []RegionWant, w RegionWant, slot string) (InteriorPiece, bool) {
	var best InteriorPiece
	bestRing, bestDist, found := -1, 0.0, false
	for _, rot := range [4]domain.Rotation{domain.North, domain.East, domain.South, domain.West} {
		for _, corner := range cells {
			p := NewInteriorPiece(slot, w.Def, w.Size, rot, corner)
			p.Row, p.Beside, p.Clear = w.Slot, w.Beside, w.Clear
			if w.Interaction != nil {
				off := *w.Interaction
				p.InteractionOffset = &off
			}
			if !regionFits(g, blocked, keep, before, thresholds, p, false) {
				continue
			}
			ring := seatsAround(g, blocked, keep, thresholds, wants, p)
			dist := 0.0
			for i, t := range thresholds {
				if d := centreDistance(p.Rect, Rectangle{X: t.X, Z: t.Z, Width: 1, Height: 1}); i == 0 || d < dist {
					dist = d
				}
			}
			if !found || ring > bestRing || ring == bestRing && dist < bestDist {
				best, bestRing, bestDist, found = p, ring, dist, true
			}
		}
	}
	return best, found
}

// frontCells are the cells just beyond the piece's footprint on the side it
// faces (rotation North faces +Z).
func (p InteriorPiece) frontCells() []domain.Cell {
	r := p.Rect
	var out []domain.Cell
	switch p.Rot {
	case domain.North:
		for x := r.X; x < r.X+r.Width; x++ {
			out = append(out, domain.Cell{X: x, Z: r.Z + r.Height})
		}
	case domain.South:
		for x := r.X; x < r.X+r.Width; x++ {
			out = append(out, domain.Cell{X: x, Z: r.Z - 1})
		}
	case domain.East:
		for z := r.Z; z < r.Z+r.Height; z++ {
			out = append(out, domain.Cell{X: r.X + r.Width, Z: z})
		}
	case domain.West:
		for z := r.Z; z < r.Z+r.Height; z++ {
			out = append(out, domain.Cell{X: r.X - 1, Z: z})
		}
	}
	return out
}

// faces reports whether the piece faces and touches one of the partners.
func (p InteriorPiece) faces(partners []InteriorPiece) bool {
	for _, c := range p.frontCells() {
		for _, q := range partners {
			if q.Row == p.Beside && rectContains(q.Rect, c) {
				return true
			}
		}
	}
	return false
}

// Reserves are the open cells the piece needs beside its footprint: its
// interaction cell and its clear lane.
func (p InteriorPiece) Reserves() []domain.Cell {
	var out []domain.Cell
	if c, ok := p.Interaction(); ok {
		out = append(out, c)
	}
	a := p.Anchor()
	for k := int32(1); k <= p.Clear; k++ {
		o := rotateOffset(domain.Cell{Z: -k}, p.Rot)
		if c := (domain.Cell{X: a.X + o.X, Z: a.Z + o.Z}); !slices.Contains(out, c) {
			out = append(out, c)
		}
	}
	return out
}

// slotName is the name of the want's i-th piece.
func (w RegionWant) slotName(i int) string {
	if i < len(w.Names) {
		return w.Names[i]
	}
	return fmt.Sprintf("%s.%d", w.Slot, i)
}

// RegionPlacement is the input to PlaceInRegion. Doors are the cells outside
// the region that open into it; the region cell beside each stays clear.
// Reserved cells take no piece. Wants place in order.
type RegionPlacement struct {
	Region   Region
	Doors    []domain.Cell
	Reserved []domain.Cell
	Wants    []RegionWant
	// Search, when set, improves the greedy layout by the seeded interior
	// search (SearchInterior) and returns the searched one, never worse than
	// the greedy layout on a gated objective term. Nil is the greedy layout.
	Search *InteriorSearch
}

// PlaceInRegion fits furniture into any region: pieces stay on region cells,
// keep every door threshold clear, give each interaction cell open floor, and
// leave all floor that was reachable from the doors still reachable. The
// result is deterministic: wants in order, wall-touching anchors before open
// floor, rotations North, East, South, West, then row-major corners. Pieces
// carry world footprints. It fails when a want places fewer than its Min.
func PlaceInRegion(req RegionPlacement) ([]InteriorPiece, error) {
	if req.Search != nil {
		res, err := SearchInterior(req, *req.Search)
		if err != nil {
			return nil, err
		}
		return res.Stood(), nil
	}
	g := req.Region
	if g.Empty() {
		return nil, fmt.Errorf("place in region: empty region")
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range req.Reserved {
		blocked[c] = true
	}
	var thresholds []domain.Cell
	for _, d := range req.Doors {
		if g.Contains(d) {
			continue
		}
		for _, n := range [4]domain.Cell{{X: d.X + 1, Z: d.Z}, {X: d.X - 1, Z: d.Z}, {X: d.X, Z: d.Z + 1}, {X: d.X, Z: d.Z - 1}} {
			if g.Contains(n) {
				thresholds = append(thresholds, n)
			}
		}
	}
	if len(req.Doors) > 0 && len(thresholds) == 0 {
		return nil, fmt.Errorf("place in region: no door opens into the region")
	}
	cells := g.Cells()
	keep := map[domain.Cell]bool{}
	for _, t := range thresholds {
		keep[t] = true
	}
	var out []InteriorPiece
	for _, w := range req.Wants {
		placed := 0
		for placed < w.Count {
			piece, ok := nextRegionPiece(g, cells, blocked, keep, thresholds, out, req.Wants, w, w.slotName(placed), placed < w.Min)
			if !ok {
				break
			}
			for _, c := range rectCells(piece.Rect) {
				blocked[c] = true
			}
			for _, c := range piece.Reserves() {
				keep[c] = true
			}
			out = append(out, piece)
			placed++
		}
		if placed < w.Min {
			return nil, fmt.Errorf("place in region: %s fits %d of the %d wanted", w.Slot, placed, w.Min)
		}
	}
	return out, nil
}

func nextRegionPiece(g Region, cells []domain.Cell, blocked, keep map[domain.Cell]bool, thresholds []domain.Cell, placed []InteriorPiece, wants []RegionWant, w RegionWant, slot string, required bool) (InteriorPiece, bool) {
	before := regionReach(g, blocked, thresholds)
	if w.Open {
		return openRegionPiece(g, cells, blocked, keep, before, thresholds, wants, w, slot)
	}
	// A want beside another seats at its partner first; only a piece the want
	// requires may then take any free cell.
	modes := []bool{false}
	if w.Beside != "" {
		modes = []bool{true}
		if required {
			modes = append(modes, false)
		}
	}
	for _, beside := range modes {
		for pass := 0; pass < 2; pass++ {
			wallPass := pass == 0
			if !wallPass && w.AgainstWall {
				break
			}
			for _, rot := range [4]domain.Rotation{domain.North, domain.East, domain.South, domain.West} {
				for _, corner := range cells {
					p := NewInteriorPiece(slot, w.Def, w.Size, rot, corner)
					p.Row, p.Beside, p.Clear = w.Slot, w.Beside, w.Clear
					if w.Interaction != nil {
						off := *w.Interaction
						p.InteractionOffset = &off
					}
					if beside && !p.faces(placed) {
						continue
					}
					if regionFits(g, blocked, keep, before, thresholds, p, wallPass) {
						return p, true
					}
				}
			}
		}
	}
	return InteriorPiece{}, false
}

func regionFits(g Region, blocked, keep, before map[domain.Cell]bool, thresholds []domain.Cell, p InteriorPiece, wallOnly bool) bool {
	touches := false
	placed := map[domain.Cell]bool{}
	for _, c := range rectCells(p.Rect) {
		if !g.Contains(c) || blocked[c] || keep[c] {
			return false
		}
		placed[c] = true
		for _, n := range [4]domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			touches = touches || !g.Contains(n)
		}
	}
	if wallOnly && !touches {
		return false
	}
	reserves := p.Reserves()
	for _, c := range reserves {
		if !g.Contains(c) || blocked[c] || placed[c] {
			return false
		}
	}
	after := map[domain.Cell]bool{}
	for c := range blocked {
		after[c] = true
	}
	for c := range placed {
		after[c] = true
	}
	reach := regionReach(g, after, thresholds)
	for c := range before {
		if !placed[c] && !reach[c] {
			return false
		}
	}
	if len(thresholds) > 0 {
		for _, c := range reserves {
			if !reach[c] {
				return false
			}
		}
	}
	return true
}

// regionReach floods the region's unblocked floor from the first free
// threshold; empty when some threshold does not join it.
func regionReach(g Region, blocked map[domain.Cell]bool, thresholds []domain.Cell) map[domain.Cell]bool {
	reach := map[domain.Cell]bool{}
	var queue []domain.Cell
	for _, t := range thresholds {
		if !blocked[t] {
			queue = append(queue, t)
			break
		}
	}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		if reach[c] || blocked[c] || !g.Contains(c) {
			continue
		}
		reach[c] = true
		queue = append(queue, domain.Cell{X: c.X + 1, Z: c.Z}, domain.Cell{X: c.X - 1, Z: c.Z}, domain.Cell{X: c.X, Z: c.Z + 1}, domain.Cell{X: c.X, Z: c.Z - 1})
	}
	for _, t := range thresholds {
		if !blocked[t] && !reach[t] {
			return map[domain.Cell]bool{}
		}
	}
	return reach
}
