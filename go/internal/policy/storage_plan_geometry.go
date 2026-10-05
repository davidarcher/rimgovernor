package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// roomCellSiteLimit bounds the one-cell candidates listed.
const roomCellSiteLimit = 16

// roomCellSites are the free roofed single cells inside room, nearest
// anchor first, never on avoid.
func roomCellSites(room []domain.Cell, anchor domain.Cell, cells []SiteCell, avoid []domain.Cell) [][]domain.Cell {
	inside := make(map[domain.Cell]bool, len(room))
	for _, c := range room {
		inside[c] = true
	}
	skip := make(map[domain.Cell]bool, len(avoid))
	for _, c := range avoid {
		skip[c] = true
	}
	var free []domain.Cell
	for _, c := range cells {
		if !inside[c.Cell] || skip[c.Cell] {
			continue
		}
		walkable, _ := c.Walkable.Value()
		roofed, _ := c.Roofed.Value()
		empty, _ := c.StorageEmpty.Value()
		occupied, ok := c.Occupied.Value()
		if walkable && roofed && empty && ok && !occupied {
			free = append(free, c.Cell)
		}
	}
	distance := func(c domain.Cell) int64 {
		dx, dz := int64(c.X-anchor.X), int64(c.Z-anchor.Z)
		return dx*dx + dz*dz
	}
	sort.Slice(free, func(i, j int) bool {
		di, dj := distance(free[i]), distance(free[j])
		if di != dj {
			return di < dj
		}
		return free[i].Z < free[j].Z || free[i].Z == free[j].Z && free[i].X < free[j].X
	})
	out := make([][]domain.Cell, 0, min(len(free), roomCellSiteLimit))
	for _, c := range free[:min(len(free), roomCellSiteLimit)] {
		out = append(out, []domain.Cell{c})
	}
	return out
}

// rawFoodStockSites finds the first planned freezer standing in the census
// and lists the 2x2 patches inside it nearest its door into the kitchen (the
// Link; the outer Door on plans saved before #819). A zero room means no
// planned freezer stands yet.
func rawFoodStockSites(layout LayoutPlan, rooms RoomObservation, bounds Bounds, cells []SiteCell, protected []domain.Cell) (Room, [][]domain.Cell, error) {
	for _, planned := range layout.AllRooms() {
		if planned.Role != PlannedFreezer {
			continue
		}
		room, ok := PlannedRoomStanding(planned, rooms)
		if !ok || len(room.Cells) == 0 {
			continue
		}
		anchor := planned.Door
		if planned.Link != nil {
			anchor = *planned.Link
		}
		sites, err := roomStorageSites(room.Cells, anchor, bounds, cells, protected)
		return room, sites, err
	}
	return Room{}, nil, nil
}

// roomStorageSites is the bounded list of free roofed 2x2 patches inside
// room, nearest anchor first; nil when nothing fits.
func roomStorageSites(room []domain.Cell, anchor domain.Cell, bounds Bounds, cells []SiteCell, protected []domain.Cell) ([][]domain.Cell, error) {
	inside := make(map[domain.Cell]bool, len(room))
	for _, cell := range room {
		inside[cell] = true
	}
	var scoped []SiteCell
	for _, cell := range cells {
		if inside[cell.Cell] {
			scoped = append(scoped, cell)
		}
	}
	if len(scoped) == 0 {
		return nil, nil
	}
	sites, err := CoveredStorageSites(CoveredStorageRequest{Bounds: bounds, Anchor: anchor, Cells: scoped, Protected: protected})
	if err != nil || len(sites) == 0 {
		return nil, err
	}
	out := make([][]domain.Cell, 0, len(sites))
	for _, site := range sites {
		var block []domain.Cell
		for x := site.X; x < site.X+site.Width; x++ {
			for z := site.Z; z < site.Z+site.Height; z++ {
				block = append(block, domain.Cell{X: x, Z: z})
			}
		}
		out = append(out, block)
	}
	return out, nil
}

// roomPool is the free roofed walkable cells inside room, never on avoid:
// what the freezer's catch-all zone may cover.
func roomPool(room []domain.Cell, cells []SiteCell, avoid []domain.Cell) []domain.Cell {
	inside := make(map[domain.Cell]bool, len(room))
	for _, c := range room {
		inside[c] = true
	}
	skip := make(map[domain.Cell]bool, len(avoid))
	for _, c := range avoid {
		skip[c] = true
	}
	var out []domain.Cell
	for _, c := range cells {
		if !inside[c.Cell] || skip[c.Cell] {
			continue
		}
		walkable, _ := c.Walkable.Value()
		roofed, _ := c.Roofed.Value()
		empty, _ := c.StorageEmpty.Value()
		occupied, ok := c.Occupied.Value()
		if walkable && roofed && empty && ok && !occupied {
			out = append(out, c.Cell)
		}
	}
	return out
}

// butcheryDoor is the planned butchery's door when it opens through a Link
// (into the freezer), else false.
func butcheryDoor(layout LayoutPlan) (domain.Cell, bool) {
	for _, r := range layout.AllRooms() {
		if r.Role == PlannedButchery && r.Link != nil {
			return *r.Link, true
		}
	}
	return domain.Cell{}, false
}
