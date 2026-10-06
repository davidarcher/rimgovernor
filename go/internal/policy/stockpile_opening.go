package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The opening stockpiles: zoning costs no pawn labor and is instant, so a
// fresh colony gets its two basic zones on the first review instead of
// waiting on a room, a deficit or a haul budget. Each is created while no
// owned zone of its kind stands (the food stockpile is a planner site,
// storage_plan_food.go):
//   - the general store, outdoors is fine, nearest the colony anchor; the
//     warehouse replaces it once the storage room stands (Store.Supersedes), so
//     none is raised while a warehouse store is declared;
//   - the corpse dump, outdoors at least openingDumpDistance from the
//     anchor and clear of living rooms.
//
// Their Hauls are zero: a creation moves nothing itself, so the haul budget
// never defers one.
const (
	openingGeneralSide  int32 = 5
	openingDumpSide     int32 = 3
	openingDumpDistance int32 = 12
)

// stockpileOpeningEdits proposes the missing opening zones. Cells taken are
// marked in open.
func stockpileOpeningEdits(r StockpileRequest, open stockpileOpen) []StockpileEdit {
	if !r.Opening || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return nil
	}
	var general, dump bool
	for _, z := range r.Zones {
		switch {
		case isWarehouseRole(z.Role) || z.Role == domain.OpeningGeneralRole || z.Filter.Base() == domain.BaseNonperishables:
			general = true
		case z.Role == domain.CorpseDumpRole:
			dump = true
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
	if !dump {
		rooms, _ := r.Rooms.Value()
		sites, err := OutdoorDumpSites(OutdoorDumpRequest{Bounds: r.Bounds, Anchor: r.Anchor, Cells: r.Cells, Rooms: rooms, Protected: dumpProtected(r.Protected, r.Planned), Width: openingDumpSide, Height: openingDumpSide, MinDistance: openingDumpDistance})
		if err == nil {
			for _, site := range sites {
				if openFree(open, site) {
					take(domain.CorpseDumpRole, domain.CorpseDumpFilter(), domain.LowPriority, site, "outdoors clear of the shelter")
					break
				}
			}
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

func openFree(open stockpileOpen, site Rectangle) bool {
	for _, c := range rectCells(site) {
		if !open.ok(c) {
			return false
		}
	}
	return true
}
