package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// reservedGround is the one record of ground a pass has already promised to a
// zone (#2189): every store sited in a pass claims its cells here, so two
// stores never overlap. A cell is claimed once and never released within the
// pass.
type reservedGround map[domain.Cell]bool

// Claim reserves cells.
func (g reservedGround) Claim(cells []domain.Cell) {
	for _, c := range cells {
		g[c] = true
	}
}

// StoreSite is one store's place: the planned room whose interior it covers,
// or a clean rectangle inside that room. The interior rectangle is the site's
// identity: a zone is matched to it by where it stands and by its role's
// prefix, never by a key carrying the census room ID that RimWorld renumbers.
// A zone is sized once at creation; moving or resizing a store is a create at
// the new site and a delete of the old zone (create before delete), never a
// patch of its geometry.
type StoreSite struct {
	// Role is the zone's role; only its prefix (before ':') identifies it.
	Role string
	// Interior is the planned room's floor, walls excluded.
	Interior Rectangle
	// Width and Height, both positive, ask for a clean rectangle of that
	// size inside Interior nearest Anchor; zero covers the whole interior.
	Width, Height int32
	Anchor        domain.Cell
	// Roofed limits a rectangle to roofed ground.
	Roofed bool
	// Avoid are interior cells a rectangle never takes (planned furniture,
	// the chairs).
	Avoid    []domain.Cell
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority

	// room, when set, stands in for Interior as the site's explicit cells, and
	// exact matches a zone by the whole role key (a bench ID, unlike a census
	// room ID that RimWorld renumbers) instead of its prefix.
	room  []domain.Cell
	exact bool
}

// footprint is the ground that identifies the site.
func (s StoreSite) footprint() []domain.Cell {
	if s.room != nil {
		return s.room
	}
	return rectCells(s.Interior)
}

// serves reports whether a zone belongs to the site: its role matches and it
// stands on the site's footprint.
func (s StoreSite) serves(z StockpileZone) bool {
	if z.Role == "" {
		return false
	}
	if s.exact && z.Role != s.Role || !s.exact && stockpileRolePrefix(z.Role) != stockpileRolePrefix(s.Role) {
		return false
	}
	return stockpileTouches(z.Cells, cellSet(s.footprint()))
}

func storeServed(zones []StockpileZone, site StoreSite) bool {
	for _, z := range zones {
		if site.serves(z) {
			return true
		}
	}
	return false
}

// Cells is the ground the site's zone would take now, or nil when none can be
// had. A whole-room cover is the largest connected set of open footprint cells
// (buildings, blocked cells and the wall ring are excluded); a rectangle is the
// free Width x Height patch inside the interior
// nearest Anchor.
func (s StoreSite) Cells(open stockpileOpen) []domain.Cell {
	if s.room == nil {
		// The zone waits until the interior is settled (#2190): no stand-in
		// store covers a room still being dug, unseen or cleared.
		reading := readInterior(s.Interior, func(c domain.Cell) (SiteCell, bool) { sc, ok := open.cells[c]; return sc, ok })
		if !reading.Ready() {
			return nil
		}
	}
	if s.Width <= 0 || s.Height <= 0 {
		// Exclusions can split either a rectangular or an explicit footprint.
		return largestComponent(coverCells(open, s.footprint()))
	}
	within := open
	within.only = cellSet(withoutCells(s.footprint(), s.Avoid))
	var allow func(SiteCell) bool
	if s.Roofed {
		allow = func(c SiteCell) bool { roofed, known := c.Roofed.Value(); return known && roofed }
	}
	sites := rectangleSites(within, s.Anchor, s.Width, s.Height, allow, 1)
	if len(sites) == 0 {
		return nil
	}
	return stockpileSorted(rectCells(sites[0]))
}

// coverCells are the open cells of room, in order.
func coverCells(open stockpileOpen, room []domain.Cell) []domain.Cell {
	var out []domain.Cell
	for _, c := range room {
		if open.ok(c) {
			out = append(out, c)
		}
	}
	return stockpileSorted(out)
}

// rectangleSites lists up to limit free width x height rectangles whose every
// cell is open and passes allow (nil: any open cell), nearest anchor first,
// ties by corner.
func rectangleSites(open stockpileOpen, anchor domain.Cell, width, height int32, allow func(SiteCell) bool, limit int) []Rectangle {
	corners := make([]domain.Cell, 0, len(open.cells))
	for p := range open.cells {
		corners = append(corners, p)
	}
	sort.Slice(corners, func(i, j int) bool { return cellLess(corners[i], corners[j]) })
	type scored struct {
		site  Rectangle
		score int64
	}
	var found []scored
	for _, p := range corners {
		site := Rectangle{p.X, p.Z, width, height}
		ok := true
		for _, c := range rectCells(site) {
			ok = ok && open.ok(c) && (allow == nil || allow(open.cells[c]))
		}
		if ok {
			found = append(found, scored{site, squaredDistance(domain.Cell{X: p.X + width/2, Z: p.Z + height/2}, anchor)})
		}
	}
	sort.SliceStable(found, func(i, j int) bool { return found[i].score < found[j].score })
	out := make([]Rectangle, 0, min(len(found), limit))
	for _, f := range found[:min(len(found), limit)] {
		out = append(out, f.site)
	}
	return out
}

// storeSiteMoves deletes the zones standing outside every site that serves
// their role: several sites may share a prefix (the warehouses), and a zone on
// any of their footprints stays. A move creates before it deletes (#1795):
// while no zone serves the new site the delete names the site's role in After,
// and the review admits it only once that create is admitted, so a deferred or
// refused create never leaves the role without a zone.
func storeSiteMoves(zones []StockpileZone, sites []StoreSite) []StockpileEdit {
	var out []StockpileEdit
	for _, z := range zones {
		var serving *StoreSite
		after, inRoom, replaced := "", false, false
		for i, site := range sites {
			roleMatch := z.Role != "" && (site.exact && z.Role == site.Role || !site.exact && stockpileRolePrefix(z.Role) == stockpileRolePrefix(site.Role))
			if !roleMatch {
				continue
			}
			if serving == nil {
				serving = &sites[i]
			}
			if site.serves(z) {
				inRoom = true
			}
			if storeServed(zones, site) {
				replaced = true
			} else if after == "" {
				after = site.Role
			}
		}
		if serving == nil || inRoom {
			continue
		}
		if replaced {
			after = ""
		}
		out = append(out, StockpileEdit{Kind: StockpileDelete, Zone: z.ID, Role: z.Role, After: after,
			Explanation: fmt.Sprintf("stockpile %s (%s): its site moved to %s, delete; %d used cells rehome", z.ID, z.Role, serving.Role, z.Used())})
	}
	return out
}

// storeSiteEdits creates a zone for every site no zone serves yet, in order,
// claiming each zone's ground in open.taken so a later site never overlaps it.
func storeSiteEdits(zones []StockpileZone, sites []StoreSite, open stockpileOpen) []StockpileEdit {
	var out []StockpileEdit
	for _, site := range sites {
		if storeServed(zones, site) {
			continue
		}
		cells := site.Cells(open)
		if len(cells) == 0 {
			continue
		}
		open.taken.Claim(cells)
		out = append(out, StockpileEdit{Kind: StockpileCreate, Role: site.Role, Cells: cells, Filter: site.Filter, Priority: site.Priority,
			Explanation: fmt.Sprintf("stockpile role %s: no zone on its site, create %d cells at (%d,%d)", site.Role, len(cells), cells[0].X, cells[0].Z)})
	}
	return out
}
