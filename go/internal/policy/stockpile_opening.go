package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The opening stockpiles: zoning costs no pawn labor and is instant, so a
// fresh colony gets its basic zone on the first review instead of
// waiting on a room or a deficit. It is created while no
// owned zone of its kind stands (the food stockpile is a planner site,
// storage_plan_food.go):
//   - the general store, outdoors is fine, nearest the colony anchor; the
//     warehouse replaces it once the storage room stands (Store.Supersedes), so
//     none is raised while a warehouse store is declared.
const (
	openingGeneralSide int32 = 5
)

// stockpileOpeningEdits proposes the missing opening zones. Cells taken are
// marked in open.
func stockpileOpeningEdits(r StockpileRequest, open stockpileOpen) []StockpileEdit {
	if !r.Opening || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return nil
	}
	var general bool
	for _, z := range r.Zones {
		if isWarehouseRole(z.Role) || z.Role == domain.OpeningGeneralRole || z.Filter.Base() == domain.BaseNonperishables {
			general = true
		}
	}
	for _, site := range r.Sited {
		general = general || isWarehouseRole(site.Role)
	}
	for _, store := range r.Stores {
		general = general || isWarehouseRole(store.Role)
	}
	var out []StockpileEdit
	take := func(role string, filter domain.StockpileFilter, priority domain.StockpilePriority, site Rectangle, where string) {
		cells := rectCells(site)
		open.taken.Claim(cells)
		out = append(out, StockpileEdit{Kind: StockpileCreate, Role: role, Cells: stockpileSorted(cells), Filter: filter, Priority: priority,
			Explanation: fmt.Sprintf("opening stockpile %s: none stands, create %dx%d at (%d,%d) %s", role, site.Width, site.Height, site.X, site.Z, where)})
	}
	if !general {
		if site, ok := openingSite(open, r.Anchor, openingGeneralSide, nil); ok {
			take(domain.OpeningGeneralRole, domain.OpeningStoreFilter(), domain.NormalPriority, site, "near the colony")
		}
	}
	return out
}

// openingSite is the free side x side square nearest anchor whose every
// cell is open and passes allow (nil: any open cell).
func openingSite(open stockpileOpen, anchor domain.Cell, side int32, allow func(SiteCell) bool) (Rectangle, bool) {
	sites := openingSites(open, anchor, side, allow, 1)
	if len(sites) == 0 {
		return Rectangle{}, false
	}
	return sites[0], true
}

// openingSites lists up to limit free side x side squares (rectangleSites).
func openingSites(open stockpileOpen, anchor domain.Cell, side int32, allow func(SiteCell) bool, limit int) []Rectangle {
	return rectangleSites(open, anchor, side, side, allow, limit)
}
