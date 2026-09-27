package policy

import (
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StockpileCreate places a missing fixed-role zone (#724).
const StockpileCreate StockpileEditKind = "create"

// stockpileCreateEdits proposes one zone for each fixed role
// (domain.GearAndDumpRoles) the colony has things for (Needs) and no zone
// of: the gear stockpiles on the free indoor roofed 2x2 patch cheapest to
// haul to from the general store (nearest the anchor without one), the
// dumps on the nearest free outdoor 2x2 patch clear of living rooms (never
// while the room census is unknown). The role's registered state, when
// published, supplies its settings; a retired role is never created. Its
// hauls are the things waiting for it. Cells taken are marked in open.
func stockpileCreateEdits(r StockpileRequest, open stockpileOpen) []StockpileEdit {
	if len(r.Needs) == 0 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return nil
	}
	have := map[string]bool{}
	var store []domain.Cell
	for _, z := range r.Zones {
		have[z.Role] = true
		if z.Role == domain.GeneralRole {
			store = append(store, z.Cells...)
		}
	}
	store = stockpileSorted(store)
	anchor := r.Anchor
	if len(store) > 0 {
		anchor = store[0]
	}
	var out []StockpileEdit
	for _, spec := range domain.GearAndDumpRoles() {
		count := r.Needs[spec.Role]
		if count <= 0 || have[spec.Role] {
			continue
		}
		filter, priority := spec.Filter, spec.Priority
		if r.Roles != nil {
			if state, ok := r.Roles(spec.Role); ok {
				if state.Retired {
					continue
				}
				filter, priority = state.Filter, state.Priority
			}
		}
		var sites []Rectangle
		var err error
		dump := spec.Priority == domain.LowPriority
		if dump {
			rooms, known := r.Rooms.Value()
			if !known {
				continue
			}
			sites, err = OutdoorDumpSites(OutdoorDumpRequest{Bounds: r.Bounds, Anchor: anchor, Cells: r.Cells, Rooms: rooms, Protected: r.Protected, Width: 2, Height: 2})
		} else {
			sites, err = stockpileGearSites(r, anchor, store)
		}
		if err != nil {
			continue
		}
		for _, site := range sites {
			cells := rectCells(site)
			free := true
			for _, c := range cells {
				free = free && open.ok(c)
			}
			if !free {
				continue
			}
			for _, c := range cells {
				open.taken[c] = true
			}
			where := "indoors near the store"
			if dump {
				where = "outdoors clear of living rooms"
			}
			out = append(out, StockpileEdit{Kind: StockpileCreate, Role: spec.Role, Cells: stockpileSorted(cells), Filter: filter, Priority: priority, Hauls: count,
				Explanation: fmt.Sprintf("stockpile role %s: %d things and no zone, create %dx%d at (%d,%d) %s", spec.Role, count, site.Width, site.Height, site.X, site.Z, where)})
			break
		}
	}
	return out
}

// StockpileSite is a role whose zone belongs in one room (#917): the meal
// shelf in the dining room, the raw-food stock in the freezer. Its absence
// is a MaintainStockpiles deficit of its own: while no zone of the role's
// prefix (a role-less legacy claim with the same settings counts) has a
// cell in Room, the first Candidate whose cells are all open is created.
// The role key carries the census room ID, which RimWorld renumbers, so a
// zone is matched by where it stands, never by the key.
type StockpileSite struct {
	Role       string
	Room       []domain.Cell
	Filter     domain.StockpileFilter
	Priority   domain.StockpilePriority
	Candidates [][]domain.Cell
}

// stockpileSiteEdits proposes a zone for every site no zone serves yet.
// Cells taken are marked in open.
func stockpileSiteEdits(r StockpileRequest, open stockpileOpen) []StockpileEdit {
	var out []StockpileEdit
	for _, site := range r.Sited {
		if stockpileSiteServed(r.Zones, site) {
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
			for _, c := range cells {
				open.taken[c] = true
			}
			out = append(out, StockpileEdit{Kind: StockpileCreate, Role: site.Role, Cells: stockpileSorted(cells), Filter: site.Filter, Priority: site.Priority, Hauls: len(cells),
				Explanation: fmt.Sprintf("stockpile role %s: no zone in its room, create %d cells at (%d,%d)", site.Role, len(cells), cells[0].X, cells[0].Z)})
			break
		}
	}
	return out
}

func stockpileSiteServed(zones []StockpileZone, site StockpileSite) bool {
	prefix, _, _ := strings.Cut(site.Role, ":")
	room := make(map[domain.Cell]bool, len(site.Room))
	for _, c := range site.Room {
		room[c] = true
	}
	for _, z := range zones {
		zonePrefix, _, _ := strings.Cut(z.Role, ":")
		if zonePrefix != prefix && (z.Role != "" || z.Filter != site.Filter || z.Priority != site.Priority) {
			continue
		}
		for _, c := range z.Cells {
			if room[c] {
				return true
			}
		}
	}
	return false
}

// stockpileRoleState is a role's published state; false for a role-less
// zone or one no owner publishes.
func stockpileRoleState(roles StockpileRoles, role string) (StockpileRoleState, bool) {
	if role == "" || roles == nil {
		return StockpileRoleState{}, false
	}
	return roles(role)
}

// stockpileGearSites are the free indoor roofed 2x2 patches nearest anchor,
// cheapest walking distance to the general store first.
func stockpileGearSites(r StockpileRequest, anchor domain.Cell, store []domain.Cell) ([]Rectangle, error) {
	covered, err := CoveredStorageSites(CoveredStorageRequest{Bounds: r.Bounds, Anchor: anchor, Cells: r.Cells, Protected: r.Protected})
	if err != nil {
		return nil, err
	}
	indoors := map[domain.Cell]bool{}
	for _, c := range r.Cells {
		indoors[c.Cell] = positive(c.Indoors)
	}
	var sites []Rectangle
	for _, site := range covered {
		ok := true
		for _, c := range rectCells(site) {
			ok = ok && indoors[c]
		}
		if ok {
			sites = append(sites, site)
		}
	}
	if len(store) == 0 || len(sites) < 2 {
		return sites, nil
	}
	costs, err := HaulCosts(r.Cells, []HaulConsumer{{Cells: store[:min(len(store), 256)], Weight: 1}})
	if err != nil {
		return nil, err
	}
	return RankSitesByHaul(sites, costs), nil
}
