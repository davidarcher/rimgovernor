package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The food stockpile (#1777): the colony's food storage zone, a 3x3 beside
// the kitchen, on roofed floor when any is free and outdoors before the
// first roof stands. It replaces the food-storage method and the
// opening food zone.
//
// The census counts a food zone as the colony's food storage only with nine
// roofed cells (the native FoodStorage fact), so the site follows the roof:
// while a roofed block is free its Room is the roofed floor, and a zone
// standing outdoors is moved onto it (stockpileSiteMoves); with none free the
// Room is the whole map, so a zone already standing is never moved and an
// outdoor one is created beside the kitchen.

const (
	// foodSiteSide is the food zone's side: nine cells, the least that
	// counts as food storage.
	foodSiteSide int32 = 3
)

// FoodStore is where the food stockpile belongs. Anchor is the cell it sits
// nearest: the cooking bench, else the colony's core.
type FoodStore struct {
	Anchor domain.Cell
}

// foodStore is the food stockpile's site: free roofed blocks outside the
// bedrooms first, then any roofed block, else any free ground, nearest the
// kitchen. The room is the first tier holding a free block, so a zone standing
// outdoors moves onto roofed floor; with none free it is the whole map, so a
// zone already standing stays and an outdoor one is created beside the kitchen.
func (r StorageRequest) foodStore() (Store, bool) {
	if r.Food == nil {
		return Store{}, false
	}
	open := newStockpileOpen(StockpileRequest{Bounds: r.Bounds, Cells: r.Cells, Protected: r.Protected})
	var sleeping map[domain.Cell]bool
	if r.Rooms != nil {
		sleeping = SleepingRoomCells(domain.Known(*r.Rooms))
	}
	roofed := func(c SiteCell) bool { return positive(c.Roofed) }
	// The room is the first tier holding a free block, the whole map last.
	var room []domain.Cell
	for _, allow := range []func(SiteCell) bool{
		func(c SiteCell) bool { return roofed(c) && !sleeping[c.Cell] },
		roofed,
		nil,
	} {
		if allow != nil && len(openingSites(open, r.Food.Anchor, foodSiteSide, allow, 1)) == 0 {
			continue
		}
		for _, c := range r.Cells {
			if allow == nil || allow(c) {
				room = append(room, c.Cell)
			}
		}
		break
	}
	site := StoreSite{Role: domain.FoodRole, Width: foodSiteSide, Height: foodSiteSide, Anchor: r.Food.Anchor, Filter: domain.FoodFilter(), Priority: domain.PreferredPriority, room: room}
	return Store{StoreSite: site}, true
}
