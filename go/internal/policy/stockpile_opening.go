package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The opening stockpiles: zoning costs no pawn labor and is instant, so a
// fresh colony gets its three basic zones on the first review instead of
// waiting on a room, a deficit or a haul budget. Each is created while no
// owned zone of its kind stands:
//   - the general store, outdoors is fine, nearest the colony anchor;
//   - the food stockpile, a 3x3 on roofed floor when there is any, else
//     beside the cooking spot; Preferred, so the indoor food zones above it
//     draw the food in once they stand;
//   - the corpse dump, outdoors at least openingDumpDistance from the
//     anchor and clear of living rooms.
//
// Their Hauls are zero: a creation moves nothing itself, so the haul budget
// never defers one.
const (
	openingGeneralSide  int32 = 5
	openingFoodSide     int32 = 3
	openingDumpSide     int32 = 3
	openingDumpDistance int32 = 12
)

// stockpileOpeningEdits proposes the missing opening zones. Cells taken are
// marked in open.
func stockpileOpeningEdits(r StockpileRequest, open stockpileOpen) []StockpileEdit {
	if !r.Opening || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return nil
	}
	var general, food, dump bool
	for _, z := range r.Zones {
		prefix := stockpileRolePrefix(z.Role) + ":"
		switch {
		case z.Role == domain.GeneralRole || z.Filter.Base() == domain.BaseNonperishables:
			general = true
		case z.Role == domain.FoodRole || z.Filter.Base() == domain.BaseFood || prefix == domain.MealsRolePrefix || prefix == domain.RawFoodRolePrefix:
			food = true
		case z.Role == domain.CorpseDumpRole:
			dump = true
		}
	}
	var out []StockpileEdit
	take := func(role string, filter domain.StockpileFilter, priority domain.StockpilePriority, site Rectangle, where string) {
		cells := rectCells(site)
		for _, c := range cells {
			open.taken[c] = true
		}
		out = append(out, StockpileEdit{Kind: StockpileCreate, Role: role, Cells: stockpileSorted(cells), Filter: filter, Priority: priority,
			Explanation: fmt.Sprintf("opening stockpile %s: none stands, create %dx%d at (%d,%d) %s", role, site.Width, site.Height, site.X, site.Z, where)})
	}
	if !general {
		if site, ok := storageRoomSite(open, r.StorageRoom, openingGeneralSide); ok {
			take(domain.GeneralRole, domain.GeneralFilter(), domain.NormalPriority, site, "in the planned storage room")
		} else if site, ok := openingSite(open, r.Anchor, openingGeneralSide, nil); ok {
			take(domain.GeneralRole, domain.GeneralFilter(), domain.NormalPriority, site, "near the colony")
		}
	}
	if !food {
		roofed := func(c SiteCell) bool { return positive(c.Roofed) }
		anchor := r.Anchor
		if r.Kitchen != nil {
			anchor = *r.Kitchen
		}
		if site, ok := openingSite(open, anchor, openingFoodSide, roofed); ok {
			take(domain.FoodRole, domain.FoodFilter(), domain.PreferredPriority, site, "on roofed floor")
		} else if site, ok := openingSite(open, anchor, openingFoodSide, nil); ok {
			take(domain.FoodRole, domain.FoodFilter(), domain.PreferredPriority, site, "beside the cooking spot")
		}
	}
	if !dump {
		rooms, _ := r.Rooms.Value()
		sites, err := OutdoorDumpSites(OutdoorDumpRequest{Bounds: r.Bounds, Anchor: r.Anchor, Cells: r.Cells, Rooms: rooms, Protected: r.Protected, Width: openingDumpSide, Height: openingDumpSide, MinDistance: openingDumpDistance})
		if err == nil {
			for _, site := range sites {
				if openFree(open, site) {
					take(domain.CorpseDumpRole, domain.CorpseDumpFilter(), domain.LowPriority, site, "outdoors clear of the shelter")
					break
				}
			}
		}
	}
	return out
}

// storageRoomSite is the side x side square inside the planned storage
// room nearest its centre whose every cell is open. A room none of whose
// cells were observed (it stands past the planning window) takes the
// centred square on trust; a room observed but blocked takes none.
func storageRoomSite(open stockpileOpen, room Rectangle, side int32) (Rectangle, bool) {
	if room.Width < side || room.Height < side {
		return Rectangle{}, false
	}
	seen := false
	for _, c := range rectCells(room) {
		if _, ok := open.cells[c]; ok {
			seen = true
			break
		}
	}
	centre := Rectangle{room.X + (room.Width-side)/2, room.Z + (room.Height-side)/2, side, side}
	if !seen {
		return centre, true
	}
	var best Rectangle
	var bestScore int64 = -1
	for x := room.X; x+side <= room.X+room.Width; x++ {
		for z := room.Z; z+side <= room.Z+room.Height; z++ {
			site := Rectangle{x, z, side, side}
			score := squaredDistance(domain.Cell{X: x, Z: z}, domain.Cell{X: centre.X, Z: centre.Z})
			if (bestScore < 0 || score < bestScore) && openFree(open, site) {
				best, bestScore = site, score
			}
		}
	}
	return best, bestScore >= 0
}

// openingSite is the free side x side square nearest anchor whose every
// cell is open and passes allow (nil: any open cell).
func openingSite(open stockpileOpen, anchor domain.Cell, side int32, allow func(SiteCell) bool) (Rectangle, bool) {
	corners := make([]domain.Cell, 0, len(open.cells))
	for p := range open.cells {
		corners = append(corners, p)
	}
	sort.Slice(corners, func(i, j int) bool { return cellLess(corners[i], corners[j]) })
	var best Rectangle
	var bestScore int64 = -1
	for _, p := range corners {
		site := Rectangle{p.X, p.Z, side, side}
		score := squaredDistance(domain.Cell{X: p.X + side/2, Z: p.Z + side/2}, anchor)
		if bestScore >= 0 && score >= bestScore || !openFree(open, site) {
			continue
		}
		ok := true
		for _, c := range rectCells(site) {
			ok = ok && (allow == nil || allow(open.cells[c]))
		}
		if ok {
			best, bestScore = site, score
		}
	}
	return best, bestScore >= 0
}

func openFree(open stockpileOpen, site Rectangle) bool {
	for _, c := range rectCells(site) {
		if !open.ok(c) {
			return false
		}
	}
	return true
}

// openingRole reports a role the opening creates, so the need-driven
// creation does not stand a second zone of it.
func openingRole(edits []StockpileEdit, role string) bool {
	for _, e := range edits {
		if e.Role == role {
			return true
		}
	}
	return false
}
