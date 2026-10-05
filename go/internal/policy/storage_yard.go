package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The materials yard (#1771): an unroofed Low priority store for items that
// are safe outside (the outdoor_safe preset), sited on free open ground
// inside the inner (core ring) enclosure, nearest the standing workshop by
// walking distance. Its site room is the enclosure's unroofed ground, so a
// zone that grows (the shared fill rule) or stands outside it is kept in or
// moved back.

// yardPatch is the side of the square patch the yard is first sited as.
const yardPatch = 2

// yardSites returns the yard site, or nil while the layout plan has no
// standing workshop or the enclosure holds no open ground. The site stays
// while its zone fills the ground (no candidates), so the zone is kept
// inside it.
func (r StorageRequest) yardSites() []StockpileSite {
	workshop, ok := r.standingWorkshop()
	if !ok || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return nil
	}
	enclosure := coreEnclosure(*r.Layout, r.Bounds.Width, r.Bounds.Height)
	fields := r.Layout.FieldCells()
	cells := make(map[domain.Cell]SiteCell, len(r.Cells))
	for _, c := range r.Cells {
		cells[c.Cell] = c
	}
	protected := cellSet(r.Protected)
	inside := func(p domain.Cell) bool {
		return p.X >= 0 && p.Z >= 0 && p.X < r.Bounds.Width && p.Z < r.Bounds.Height && enclosure.in[p.Z*r.Bounds.Width+p.X]
	}
	not := func(v bool) bool { return !v }
	open := func(p domain.Cell) bool {
		c, ok := cells[p]
		return ok && inside(p) && !fields[p] && positive(measured(c.Roofed, not)) && positive(measured(c.Indoors, not))
	}
	free := func(p domain.Cell) bool {
		c := cells[p]
		return open(p) && !protected[p] && positive(c.Walkable) && positive(c.StorageEmpty) &&
			positive(measured(c.Occupied, not)) && positive(measured(c.Zone, not))
	}
	var room []domain.Cell
	for _, c := range r.Cells {
		if open(c.Cell) {
			room = append(room, c.Cell)
		}
	}
	distance := walkingDistances(workshop, cells)
	type patch struct {
		cells []domain.Cell
		dist  int
	}
	var patches []patch
	for _, p := range room {
		block := rectCells(Rectangle{X: p.X, Z: p.Z, Width: yardPatch, Height: yardPatch})
		best, legal := -1, true
		for _, q := range block {
			d, reached := distance[q]
			if !free(q) || !reached {
				legal = false
				break
			}
			if best < 0 || d < best {
				best = d
			}
		}
		if legal {
			patches = append(patches, patch{block, best})
		}
	}
	if len(room) == 0 {
		return nil
	}
	sort.Slice(patches, func(i, j int) bool {
		if patches[i].dist != patches[j].dist {
			return patches[i].dist < patches[j].dist
		}
		return cellLess(patches[i].cells[0], patches[j].cells[0])
	})
	site := StockpileSite{Role: domain.YardRole, Room: room, Filter: domain.YardFilter(), Priority: domain.LowPriority}
	for _, p := range patches {
		site.Candidates = append(site.Candidates, p.cells)
	}
	return []StockpileSite{site}
}

// standingWorkshop is the cells of the first standing planned workshop.
func (r StorageRequest) standingWorkshop() ([]domain.Cell, bool) {
	for _, planned := range r.Layout.AllRooms() {
		if planned.Role != PlannedWorkshop {
			continue
		}
		// Census: the yard abuts the room's own cells.
		if room, ok := CensusRoomIn(planned, *r.Rooms); ok && len(room.Cells) > 0 {
			return room.Cells, true
		}
	}
	return nil, false
}

// walkingDistances is the four-connected step count over walkable cells
// from the source cells (distance 0, walkable or not); cells it does not
// reach are absent.
func walkingDistances(source []domain.Cell, cells map[domain.Cell]SiteCell) map[domain.Cell]int {
	dist := make(map[domain.Cell]int, len(cells))
	frontier := stockpileSorted(source)
	for _, c := range frontier {
		dist[c] = 0
	}
	for step := 1; len(frontier) > 0; step++ {
		var next []domain.Cell
		for _, c := range frontier {
			for _, n := range stockpileNeighbours(c) {
				if _, seen := dist[n]; seen || !positive(cells[n].Walkable) {
					continue
				}
				dist[n] = step
				next = append(next, n)
			}
		}
		frontier = next
	}
	return dist
}
