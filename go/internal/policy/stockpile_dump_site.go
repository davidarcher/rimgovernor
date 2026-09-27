package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OutdoorDumpRequest is the cell and room census an outdoor dump (#724:
// rotten items and human corpses) is sited from.
type OutdoorDumpRequest struct {
	Bounds        Bounds
	Anchor        domain.Cell
	Cells         []SiteCell
	Rooms         []Room
	Protected     []domain.Cell
	Width, Height int32
	// MinDistance, when set, keeps every site at least this Chebyshev
	// distance from the anchor (the opening corpse dump, clear of the shelter).
	MinDistance int32
}

// outdoorDumpClearance is the Chebyshev distance an outdoor dump keeps from
// every cell of a room colonists sleep or eat in.
const outdoorDumpClearance int32 = 6

// livingRoomRoles are the rooms colonists sleep or eat in; a dump never
// stands in or beside one.
var livingRoomRoles = map[RoomRole]bool{
	RoomRoleBedroom: true, RoomRoleBarracks: true, RoomRoleDiningRoom: true,
	RoomRoleKitchen: true, RoomRoleHospital: true, RoomRoleRecRoom: true,
}

// OutdoorDumpSites returns at most eight Width x Height rectangles, nearest
// the anchor first, whose every cell is walkable, outdoors, unroofed,
// unoccupied, unzoned and empty, and at least outdoorDumpClearance cells
// from any living room. A room whose role is unknown counts as living.
// Unknown cells are never free; native previews still decide legality.
func OutdoorDumpSites(r OutdoorDumpRequest) ([]Rectangle, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || len(r.Cells) > 65536 || len(r.Protected) > 65536 || r.Width <= 0 || r.Height <= 0 || r.Width > 16 || r.Height > 16 {
		return nil, errors.New("invalid outdoor dump request")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if !inBounds(r.Anchor) {
		return nil, errors.New("invalid outdoor dump anchor")
	}
	cells := make(map[domain.Cell]SiteCell, len(r.Cells))
	for _, c := range r.Cells {
		if !inBounds(c.Cell) {
			return nil, errors.New("outdoor dump cell out of bounds")
		}
		if _, dup := cells[c.Cell]; dup {
			return nil, errors.New("duplicate outdoor dump cell")
		}
		cells[c.Cell] = c
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		blocked[c] = true
	}
	// Every cell within the clearance of a living room is blocked.
	for _, room := range r.Rooms {
		if role, known := room.Role.Value(); known && !livingRoomRoles[role] {
			continue
		}
		for _, l := range room.Cells {
			for dx := -outdoorDumpClearance + 1; dx < outdoorDumpClearance; dx++ {
				for dz := -outdoorDumpClearance + 1; dz < outdoorDumpClearance; dz++ {
					blocked[domain.Cell{X: l.X + dx, Z: l.Z + dz}] = true
				}
			}
		}
	}
	free := func(p domain.Cell) bool {
		c, ok := cells[p]
		if !ok || blocked[p] {
			return false
		}
		not := func(v bool) bool { return !v }
		return positive(c.Walkable) && positive(c.StorageEmpty) &&
			positive(measured(c.Indoors, not)) && positive(measured(c.Roofed, not)) &&
			positive(measured(c.Occupied, not)) && positive(measured(c.Zone, not))
	}
	type site struct {
		score int64
		cell  domain.Cell
	}
	var sites []site
	for p := range cells {
		if p.X+r.Width > r.Bounds.Width || p.Z+r.Height > r.Bounds.Height {
			continue
		}
		legal := true
		for _, q := range rectCells(Rectangle{p.X, p.Z, r.Width, r.Height}) {
			if !free(q) {
				legal = false
				break
			}
		}
		if legal {
			if r.MinDistance > 0 && max(absInt32(p.X-r.Anchor.X), absInt32(p.Z-r.Anchor.Z)) < r.MinDistance {
				continue
			}
			sites = append(sites, site{squaredDistance(p, r.Anchor), p})
		}
	}
	sort.Slice(sites, func(i, j int) bool {
		if sites[i].score != sites[j].score {
			return sites[i].score < sites[j].score
		}
		return cellLess(sites[i].cell, sites[j].cell)
	})
	if len(sites) > 8 {
		sites = sites[:8]
	}
	out := make([]Rectangle, len(sites))
	for i, s := range sites {
		out[i] = Rectangle{s.cell.X, s.cell.Z, r.Width, r.Height}
	}
	return out, nil
}
