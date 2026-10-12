package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A store's zone follows its room's region. A room that grows is filled by
// storeGeometryEdits (cells added to the standing zone), a room that is
// replaced moves the zone to the successor's ground (storeSiteMoves: the
// replacement is created with the declared filter and priority before the old
// zone is deleted, matched by position), and a room that shrinks, or whose
// floor furniture now stands on, loses exactly those cells here. Stock on a
// given-up cell drains by vanilla hauling: no haul action, no fill memory.

// furnished reports a cell holding furniture a zone must stay off: a
// building, blueprint or frame that is not a shelf (a shelf is the zone's own
// storage).
func furnished(c SiteCell) bool {
	for _, t := range c.Things {
		if t.Flags&(FlagEdifice|FlagBlueprint|FlagFrame) != 0 && t.Def != ShelfDefinition {
			return true
		}
	}
	return false
}

// storeShrinkEdits cuts each zone of a whole-footprint store back to the
// ground its serving sites still cover: cells outside every serving site's
// footprint (the room shrank or reshaped), in a site's Avoid list, or under
// furniture are removed. A zone left with no cell is not shrunk: it is moved
// or deleted by storeSiteMoves. Zones with a settings edit pending wait.
func storeShrinkEdits(zones []StockpileZone, sites []StoreSite, open stockpileOpen, settings []StockpileEdit) []StockpileEdit {
	pending := map[string]bool{}
	for _, e := range settings {
		pending[e.Zone] = true
	}
	var out []StockpileEdit
	for _, z := range zones {
		if pending[z.ID] || z.Role == "" {
			continue
		}
		keep := map[domain.Cell]bool{}
		whole := false
		for _, site := range sites {
			if !site.wholeFootprint() || site.regions != nil || !site.serves(z) {
				continue
			}
			whole = true
			for _, c := range withoutCells(site.footprint(), site.Avoid) {
				keep[c] = true
			}
		}
		if !whole {
			continue
		}
		var drop []domain.Cell
		for _, c := range z.Cells {
			if !keep[c] || furnished(open.cells[c]) {
				drop = append(drop, c)
			}
		}
		if len(drop) == 0 || len(drop) == len(z.Cells) {
			continue
		}
		out = append(out, StockpileEdit{Kind: StockpileShrink, Zone: z.ID, Role: z.Role, RemovedCells: stockpileSorted(drop),
			Explanation: fmt.Sprintf("stockpile %s (%s): room gave up %d cells, remove them; stock there drains by vanilla hauling", z.ID, z.Role, len(drop))})
	}
	return out
}

// connectedComponents splits cells into cardinally connected parts, each
// sorted, ordered by their lowest cell.
func connectedComponents(cells []domain.Cell) [][]domain.Cell {
	member := cellSet(cells)
	seen := map[domain.Cell]bool{}
	var out [][]domain.Cell
	for _, start := range stockpileSorted(cells) {
		if seen[start] {
			continue
		}
		seen[start] = true
		part := []domain.Cell{start}
		for i := 0; i < len(part); i++ {
			for _, n := range stockpileNeighbours(part[i]) {
				if member[n] && !seen[n] {
					seen[n] = true
					part = append(part, n)
				}
			}
		}
		sort.Slice(part, func(i, j int) bool { return cellLess(part[i], part[j]) })
		out = append(out, part)
	}
	return out
}
