package policy

import (
	"fmt"

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
