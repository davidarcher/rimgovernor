package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// storeGeometryEdits fills cleared ground reachable from a whole-footprint
// store's first owned zone. Once that growth stands, compatible adjacent
// fragments are deleted; the following review adds their freed cells to the
// survivor. Blockers and unrelated zones keep fragments separate until the
// ground connects them. Native zoneability and connected-shape checks still
// govern every write.
func storeGeometryEdits(zones []StockpileZone, sites []StoreSite, open stockpileOpen, settings []StockpileEdit) []StockpileEdit {
	pending := map[string]bool{}
	for _, edit := range settings {
		pending[edit.Zone] = true
	}
	var edits []StockpileEdit
	for _, site := range sites {
		if site.Width > 0 || site.Height > 0 {
			continue
		}
		var fragments []StockpileZone
		settingsPending := false
		for _, zone := range zones {
			if site.serves(zone) {
				fragments = append(fragments, zone)
				settingsPending = settingsPending || pending[zone.ID]
			}
		}
		if len(fragments) == 0 || settingsPending {
			continue
		}
		sort.Slice(fragments, func(i, j int) bool { return fragments[i].ID < fragments[j].ID })
		keep := fragments[0]
		footprint := cellSet(withoutCells(site.footprint(), site.Avoid))
		owned := cellSet(keep.Cells)
		allowed := func(cell domain.Cell) bool {
			if !footprint[cell] || open.protected[cell] || open.taken[cell] {
				return false
			}
			observed, known := open.cells[cell]
			if !known {
				return false
			}
			if site.Roofed {
				roofed, known := observed.Roofed.Value()
				if !known || !roofed {
					return false
				}
			}
			return true
		}
		reached := map[domain.Cell]bool{}
		var queue []domain.Cell
		for _, cell := range keep.Cells {
			if allowed(cell) {
				reached[cell] = true
				queue = append(queue, cell)
			}
		}
		var add []domain.Cell
		for i := 0; i < len(queue); i++ {
			for _, cell := range stockpileNeighbours(queue[i]) {
				if reached[cell] || !allowed(cell) || !owned[cell] && !open.ok(cell) {
					continue
				}
				reached[cell] = true
				queue = append(queue, cell)
				if !owned[cell] {
					add = append(add, cell)
				}
			}
		}
		if len(add) > 0 {
			for _, cell := range add {
				open.taken[cell] = true
			}
			edits = append(edits, StockpileEdit{Kind: StockpileGrow, Zone: keep.ID, Role: keep.Role, AddedCells: stockpileSorted(add),
				Explanation: fmt.Sprintf("stockpile %s (%s): fill %d cleared site cells", keep.ID, keep.Role, len(add))})
			continue
		}
		for _, fragment := range fragments[1:] {
			if fragment.Filter != keep.Filter || fragment.Priority != keep.Priority {
				continue
			}
			connected, clear := false, true
			for _, cell := range fragment.Cells {
				observed := open.cells[cell]
				walkable, known := observed.Walkable.Value()
				clear = clear && allowed(cell) && known && walkable && !observed.Occupied()
				for _, neighbour := range stockpileNeighbours(cell) {
					connected = connected || reached[neighbour]
				}
			}
			if clear && connected {
				edits = append(edits, StockpileEdit{Kind: StockpileDelete, Zone: fragment.ID, Role: fragment.Role,
					Explanation: fmt.Sprintf("stockpile %s (%s): consolidate into %s", fragment.ID, fragment.Role, keep.ID)})
			}
		}
	}
	return edits
}
