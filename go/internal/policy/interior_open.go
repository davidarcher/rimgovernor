package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// InteriorReading classifies a planned room's interior cells against the
// census cells (#2190). A store's zone is created only when every interior
// cell is settled: walkable (Open), or a Kept cell.
type InteriorReading struct {
	// Open cells are walkable.
	Open []domain.Cell
	// Dig cells are natural rock still to be mined.
	Dig []domain.Cell
	// Pending cells are unseen (fogged, unknown) or hold a ruin that
	// clearance still removes: they may turn out open.
	Pending []domain.Cell
	// Kept cells are seen, not walkable, and neither rock nor clearable
	// (water, a boulder-free edifice, a tree): the room keeps them and the
	// zone leaves them out.
	Kept []domain.Cell
}

// InteriorOf reads interior against cells, in reading order.
func InteriorOf(interior Rectangle, cells []SiteCell) InteriorReading {
	site := make(map[domain.Cell]SiteCell, len(cells))
	for _, c := range cells {
		site[c.Cell] = c
	}
	return readInterior(interior, func(c domain.Cell) (SiteCell, bool) { s, ok := site[c]; return s, ok })
}

func readInterior(interior Rectangle, lookup func(domain.Cell) (SiteCell, bool)) InteriorReading {
	var r InteriorReading
	for _, cell := range rectCells(interior) {
		c, listed := lookup(cell)
		walkable, known := c.Walkable.Value()
		ruin := c.Ruin()
		switch {
		case !listed || !known:
			r.Pending = append(r.Pending, cell)
		case walkable:
			r.Open = append(r.Open, cell)
		case rockCell(c):
			r.Dig = append(r.Dig, cell)
		case ruin:
			r.Pending = append(r.Pending, cell)
		default:
			r.Kept = append(r.Kept, cell)
		}
	}
	return r
}

// Ready is true when no interior cell is left to dig, unseen or to clear, and
// at least one is open: every cell is walkable or kept.
func (r InteriorReading) Ready() bool {
	return len(r.Dig) == 0 && len(r.Pending) == 0 && len(r.Open) > 0
}

// Cover is the walkable cells a zone takes. Native refuses a non-contiguous
// zone, so a kept cell that splits the open cells leaves the largest
// cardinally connected part (the one holding the lowest cell on a tie).
func (r InteriorReading) Cover() []domain.Cell { return largestComponent(r.Open) }

// largestComponent is the largest cardinally connected part of cells, sorted.
func largestComponent(cells []domain.Cell) []domain.Cell {
	member := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		member[c] = true
	}
	sorted := stockpileSorted(cells)
	seen := make(map[domain.Cell]bool, len(cells))
	var best []domain.Cell
	for _, start := range sorted {
		if seen[start] {
			continue
		}
		seen[start] = true
		part, queue := []domain.Cell{start}, []domain.Cell{start}
		for len(queue) > 0 {
			c := queue[0]
			queue = queue[1:]
			for _, n := range stockpileNeighbours(c) {
				if member[n] && !seen[n] {
					seen[n] = true
					part = append(part, n)
					queue = append(queue, n)
				}
			}
		}
		if len(part) > len(best) {
			best = part
		}
	}
	sort.Slice(best, func(i, j int) bool { return cellLess(best[i], best[j]) })
	return best
}

// InteriorOpen is the planned room's interior reading against the census.
func (r PlannedRoom) InteriorOpen(cells []SiteCell) InteriorReading {
	return InteriorOf(r.Interior, cells)
}

// IsStoreRoom reports whether role is a room whose purpose is a store, the dug
// rooms whose excavation is a priority (#2190).
func IsStoreRoom(role PlannedRole) bool {
	return role == PlannedStorage || role == PlannedArmory || role == PlannedWardrobe
}
