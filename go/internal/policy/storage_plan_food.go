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
	// foodSiteCandidates bounds the blocks listed.
	foodSiteCandidates = 8
)

// FoodStore is where the food stockpile belongs. Anchor is the cell it sits
// nearest: the cooking bench, else the colony's core.
type FoodStore struct {
	Anchor domain.Cell
}

// foodSites is the food stockpile: free roofed blocks outside the bedrooms
// first, then any roofed block, else any free ground, nearest the kitchen.
func (r StorageRequest) foodSites() []StockpileSite {
	if r.Food == nil {
		return nil
	}
	open := newStockpileOpen(StockpileRequest{Bounds: r.Bounds, Cells: r.Cells, Protected: r.Protected})
	var sleeping map[domain.Cell]bool
	if r.Rooms != nil {
		sleeping = SleepingRoomCells(domain.Known(*r.Rooms))
	}
	roofed := func(c SiteCell) bool { return positive(c.Roofed) }
	var blocks []Rectangle
	for _, allow := range []func(SiteCell) bool{
		func(c SiteCell) bool { return roofed(c) && !sleeping[c.Cell] },
		roofed,
	} {
		blocks = append(blocks, openingSites(open, r.Food.Anchor, foodSiteSide, allow, foodSiteCandidates)...)
	}
	room := make([]domain.Cell, 0, len(r.Cells))
	if len(blocks) > 0 {
		for _, c := range r.Cells {
			if roofed(c) {
				room = append(room, c.Cell)
			}
		}
	} else {
		blocks = openingSites(open, r.Food.Anchor, foodSiteSide, nil, foodSiteCandidates)
		for _, c := range r.Cells {
			room = append(room, c.Cell)
		}
	}
	site := StockpileSite{Role: domain.FoodRole, Room: room, Filter: domain.FoodFilter(), Priority: domain.PreferredPriority}
	seen := map[Rectangle]bool{}
	for _, block := range blocks {
		if !seen[block] && len(site.Candidates) < foodSiteCandidates {
			seen[block] = true
			site.Candidates = append(site.Candidates, stockpileSorted(rectCells(block)))
		}
	}
	return []StockpileSite{site}
}
