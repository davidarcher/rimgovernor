package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The warehouse (#1770): the general role is a roofed store inside the
// standing storage room, Low priority so the workstation stockpiles draw
// items first, growing by the fill rule (an indoor-only zone grows onto
// roofed cells only). It starts as a free square of the opening
// store's size, else a 2x2 patch. It supersedes the opening outdoor store,
// which is deleted, its items rehoming within the haul budget.

// warehouseSites is the warehouse site of each standing storage room: the
// first planned room's is the "general" role, a further room's (#1772) is
// "general:<room id>"; every site supersedes the opening store.
func (r StorageRequest) warehouseSites() []StockpileSite {
	var sites []StockpileSite
	for i, planned := range r.plannedStorageRooms() {
		// Census: the stockpile zone is the room's own cells.
		room, ok := CensusRoomIn(planned, *r.Rooms)
		if !ok || len(room.Cells) == 0 {
			continue
		}
		centre := centroidOf(room.Cells)
		candidates := roomSquares(roomPool(room.Cells, r.Cells, r.Protected), centre, openingGeneralSide)
		if small, err := roomStorageSites(room.Cells, centre, r.Bounds, r.Cells, r.Protected); err == nil {
			candidates = append(candidates, small...)
		}
		site := StockpileSite{Role: domain.GeneralRole, Room: room.Cells, Filter: domain.GeneralFilter(), Priority: domain.LowPriority,
			Candidates: candidates, Supersedes: domain.OpeningGeneralRole}
		if i > 0 {
			site.Role = domain.GeneralRole + ":" + room.ID
		}
		sites = append(sites, site)
	}
	return sites
}

// roomSquares are the side x side squares wholly inside pool, nearest
// centre first, bounded by roomCellSiteLimit.
func roomSquares(pool []domain.Cell, centre domain.Cell, side int32) [][]domain.Cell {
	free := make(map[domain.Cell]bool, len(pool))
	for _, c := range pool {
		free[c] = true
	}
	var corners []domain.Cell
	for _, c := range pool {
		fits := true
		for dx := int32(0); dx < side && fits; dx++ {
			for dz := int32(0); dz < side && fits; dz++ {
				fits = free[domain.Cell{X: c.X + dx, Z: c.Z + dz}]
			}
		}
		if fits {
			corners = append(corners, c)
		}
	}
	middle := func(c domain.Cell) int64 {
		return squaredDistance(domain.Cell{X: c.X + side/2, Z: c.Z + side/2}, centre)
	}
	sort.Slice(corners, func(i, j int) bool {
		if di, dj := middle(corners[i]), middle(corners[j]); di != dj {
			return di < dj
		}
		return cellLess(corners[i], corners[j])
	})
	var out [][]domain.Cell
	for _, c := range corners[:min(len(corners), roomCellSiteLimit)] {
		square := make([]domain.Cell, 0, side*side)
		for dx := int32(0); dx < side; dx++ {
			for dz := int32(0); dz < side; dz++ {
				square = append(square, domain.Cell{X: c.X + dx, Z: c.Z + dz})
			}
		}
		out = append(out, square)
	}
	return out
}

func centroidOf(cells []domain.Cell) domain.Cell {
	var sx, sz int64
	for _, c := range cells {
		sx += int64(c.X)
		sz += int64(c.Z)
	}
	return domain.Cell{X: int32(sx / int64(len(cells))), Z: int32(sz / int64(len(cells)))}
}

// stockpileSupersededDeletes deletes the zones a served site supersedes.
func stockpileSupersededDeletes(r StockpileRequest) []StockpileEdit {
	var out []StockpileEdit
	for _, site := range r.Sited {
		if site.Supersedes == "" || !stockpileSiteServed(r.Zones, site) {
			continue
		}
		for _, z := range r.Zones {
			if z.Role != "" && stockpileRolePrefix(z.Role) == site.Supersedes {
				out = append(out, StockpileEdit{Kind: StockpileDelete, Zone: z.ID, Role: z.Role, Hauls: z.Used(),
					Explanation: fmt.Sprintf("stockpile %s (%s): superseded by %s, delete; %d used cells rehome", z.ID, z.Role, site.Role, z.Used())})
			}
		}
	}
	return out
}
