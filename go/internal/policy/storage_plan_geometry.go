package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// roomCellSiteLimit bounds the one-cell candidates listed.
const roomCellSiteLimit = 16

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
