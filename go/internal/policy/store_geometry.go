package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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
		if walkable && roofed && empty && !c.Occupied() {
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
