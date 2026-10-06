package policy

import (
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StockpileCreate places a missing fixed-role zone (#724).
const StockpileCreate StockpileEditKind = "create"

// StockpileSite is a role whose zone belongs in one room (#917): the meal
// stockpile where the meals keep (#936), the raw-food stock in the freezer.
// Its absence is a MaintainStockpiles deficit of its own: while no zone of
// the role's prefix has a cell in Room, the first Candidate whose cells are
// all open is created. A zone of the prefix with no cell in Room is deleted
// (the site moved). The role key carries the census room ID, which
// RimWorld renumbers, so a zone is matched by where it stands, never by the key.
type StockpileSite struct {
	Role       string
	Room       []domain.Cell
	Filter     domain.StockpileFilter
	Priority   domain.StockpilePriority
	Candidates [][]domain.Cell
	// Remainder makes the site a catch-all: Candidates[0] is the pool of cells it
	// may take, and the zone created is every pool cell still open once the
	// sites ahead of it have taken theirs.
	Remainder bool
	// Keyed makes Role a stable identity (a bench ID, unlike a census room
	// ID that RimWorld renumbers): zones are matched to the site by the
	// whole role key, so several sites may share a prefix.
	Keyed bool
}

// stockpileSiteMoves is storeSiteMoves over the planner's sites.
func stockpileSiteMoves(r StockpileRequest) []StockpileEdit {
	return storeSiteMoves(r.Zones, storeSites(r.Sited))
}

func storeSites(sited []StockpileSite) []StoreSite {
	out := make([]StoreSite, len(sited))
	for i, s := range sited {
		out[i] = s.store()
	}
	return out
}

// isWarehouseRole reports a warehouse zone's role: every warehouse site is a
// general store, the first planned room's "general" or a further one's
// "general:<room id>" (#1772, #1798).
func isWarehouseRole(role string) bool { return stockpileRolePrefix(role) == domain.GeneralRole }

func stockpileRolePrefix(role string) string {
	prefix, _, _ := strings.Cut(role, ":")
	return prefix
}

func cellSet(cells []domain.Cell) map[domain.Cell]bool {
	out := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		out[c] = true
	}
	return out
}

func stockpileTouches(cells []domain.Cell, room map[domain.Cell]bool) bool {
	for _, c := range cells {
		if room[c] {
			return true
		}
	}
	return false
}

// stockpileSiteEdits proposes a zone for every site no zone serves yet.
// Cells taken are marked in open.
func stockpileSiteEdits(r StockpileRequest, open stockpileOpen) []StockpileEdit {
	var out []StockpileEdit
	for _, site := range r.Sited {
		if stockpileSiteServed(r.Zones, site) {
			continue
		}
		if site.Remainder {
			var cells []domain.Cell
			if len(site.Candidates) > 0 {
				for _, c := range site.Candidates[0] {
					if open.ok(c) {
						cells = append(cells, c)
					}
				}
			}
			if len(cells) == 0 {
				continue
			}
			open.taken.Claim(cells)
			out = append(out, StockpileEdit{Kind: StockpileCreate, Role: site.Role, Cells: stockpileSorted(cells), Filter: site.Filter, Priority: site.Priority,
				Explanation: fmt.Sprintf("stockpile role %s: no zone in its room, create the remaining %d cells", site.Role, len(cells))})
			continue
		}
		for _, cells := range site.Candidates {
			free := len(cells) > 0
			for _, c := range cells {
				free = free && open.ok(c)
			}
			if !free {
				continue
			}
			open.taken.Claim(cells)
			out = append(out, StockpileEdit{Kind: StockpileCreate, Role: site.Role, Cells: stockpileSorted(cells), Filter: site.Filter, Priority: site.Priority,
				Explanation: fmt.Sprintf("stockpile role %s: no zone in its room, create %d cells at (%d,%d)", site.Role, len(cells), cells[0].X, cells[0].Z)})
			break
		}
	}
	return out
}

func stockpileSiteServed(zones []StockpileZone, site StockpileSite) bool {
	return storeServed(zones, site.store())
}
