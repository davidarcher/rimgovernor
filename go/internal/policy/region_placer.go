package policy

import (
	"fmt"

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
	// AgainstWall requires a footprint cell beside a cell outside the region
	// (a wall, or the edge of a hole). Without it the piece still prefers a
	// wall cell and falls back to the open floor.
	AgainstWall bool
}

// RegionPlacement is the input to PlaceInRegion. Doors are the cells outside
// the region that open into it; the region cell beside each stays clear.
// Reserved cells take no piece. Wants place in order.
type RegionPlacement struct {
	Region   Region
	Doors    []domain.Cell
	Reserved []domain.Cell
	Wants    []RegionWant
}

// PlaceInRegion fits furniture into any region: pieces stay on region cells,
// keep every door threshold clear, give each interaction cell open floor, and
// leave all floor that was reachable from the doors still reachable. The
// result is deterministic: wants in order, wall-touching anchors before open
// floor, rotations North, East, South, West, then row-major corners. Pieces
// carry world footprints. It fails when a want places fewer than its Min.
func PlaceInRegion(req RegionPlacement) ([]InteriorPiece, error) {
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
			piece, ok := nextRegionPiece(g, cells, blocked, keep, thresholds, w, fmt.Sprintf("%s.%d", w.Slot, placed))
			if !ok {
				break
			}
			for _, c := range rectCells(piece.Rect) {
				blocked[c] = true
			}
			if c, has := piece.Interaction(); has {
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

func nextRegionPiece(g Region, cells []domain.Cell, blocked, keep map[domain.Cell]bool, thresholds []domain.Cell, w RegionWant, slot string) (InteriorPiece, bool) {
	before := regionReach(g, blocked, thresholds)
	for pass := 0; pass < 2; pass++ {
		wallPass := pass == 0
		if !wallPass && w.AgainstWall {
			break
		}
		for _, rot := range [4]domain.Rotation{domain.North, domain.East, domain.South, domain.West} {
			for _, corner := range cells {
				p := NewInteriorPiece(slot, w.Def, w.Size, rot, corner)
				p.Row = w.Slot
				if w.Interaction != nil {
					off := *w.Interaction
					p.InteractionOffset = &off
				}
				if regionFits(g, blocked, keep, before, thresholds, p, wallPass) {
					return p, true
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
	inter, hasInter := p.Interaction()
	if hasInter && (!g.Contains(inter) || blocked[inter] || placed[inter]) {
		return false
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
	return !hasInter || len(thresholds) == 0 || reach[inter]
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
