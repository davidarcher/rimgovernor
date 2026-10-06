package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Shelves (#721). Core's Shelf (Buildings_Furniture.xml, StorageShelfBase)
// is a 2x1 Building_Storage that holds building.maxItemsInCell = 3 stacks
// per cell, stops deterioration on top, requires the ComplexFurniture
// research and cannot overlap zones: when its blueprint spawns on a
// stockpile the zone gives up those cells (ZoneManager
// .Notify_NoZoneOverlapThingSpawned), so a shelf sited inside a zone trades
// its two floor stacks for six shelf stacks. Its default settings (Preferred,
// every category) are replaced by the served zone's desired filter and
// priority by MaintainStockpiles (StockpileShelfPatch).
const (
	ShelfDefinition   = "Shelf"
	ShelfItemsPerCell = 3
)

// ShelfRole is the stockpile role key a configured shelf is claimed by.
func ShelfRole(building string) string { return "shelf:" + building }

// ShelfZone is an owned stockpile zone shelves may serve: its census cells
// now and the filter and priority it was created with.
type ShelfZone struct {
	Zone     string
	Cells    []domain.Cell
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority
}

// ShelfRecord is one shelf the planner placed for a zone. Building is the
// built shelf's identity, empty while it is not (yet) built; Open marks a
// placement still in flight.
type ShelfRecord struct {
	Zone     string
	Building string
	Cells    []domain.Cell
	Open     bool
}

type ShelfStepKind string

const (
	ShelfNone  ShelfStepKind = ""
	ShelfBuild ShelfStepKind = "build"
)

// ShelfStep is the next shelf action: place another shelf inside a zone
// (Pieces are the candidate sites in preference order; the caller previews
// them in turn).
type ShelfStep struct {
	Kind   ShelfStepKind
	Zone   ShelfZone
	Pieces []InteriorPiece
}

// ShelfRequest is the planner's view: the zones, the shelves already placed,
// the site census and whether the shelf is buildable (researched).
type ShelfRequest struct {
	Zones     []ShelfZone
	Shelves   []ShelfRecord
	Cells     []SiteCell
	Available bool
}

// maxShelfSites bounds the sites one build step previews.
const maxShelfSites = 4

// NextShelfStep builds one shelf at a time: an open placement waits; then
// the first zone (by id) under its shelf quota gets candidate sites.
func NextShelfStep(r ShelfRequest) ShelfStep {
	zones := append([]ShelfZone(nil), r.Zones...)
	sort.Slice(zones, func(i, j int) bool { return zones[i].Zone < zones[j].Zone })
	for _, s := range r.Shelves {
		if s.Open {
			return ShelfStep{}
		}
	}
	if !r.Available {
		return ShelfStep{}
	}
	for _, z := range zones {
		shelfCells := 0
		for _, s := range r.Shelves {
			if s.Zone == z.Zone && s.Building != "" {
				shelfCells += len(s.Cells)
			}
		}
		if shelfCells+2 > ShelfQuotaCells(len(z.Cells)+shelfCells) {
			continue
		}
		if pieces := ShelfSites(z.Cells, r.Cells); len(pieces) > 0 {
			return ShelfStep{Kind: ShelfBuild, Zone: z, Pieces: pieces}
		}
	}
	return ShelfStep{}
}

// ShelfQuotaCells is how many of a stockpile footprint's cells may carry
// shelves: a third, in whole 2-cell shelves, and at least one shelf from a
// 2x2 up, so floor aisles and loose-item cells remain.
func ShelfQuotaCells(footprint int) int {
	if footprint < 4 {
		return 0
	}
	return max(2, footprint/3/2*2)
}

// ShelfSites lists the 2x1 shelf placements inside a zone's cells, the
// wall-backed ones first. A site's cells must be free, walkable floor away
// from any doorway; every shelf cell must keep a walkable zone neighbour to
// be reached from; and the zone left over must stay non-empty and
// contiguous, since a split zone is not the zone the planner owns.
func ShelfSites(zone []domain.Cell, cells []SiteCell) []InteriorPiece {
	in := map[domain.Cell]bool{}
	for _, c := range zone {
		in[c] = true
	}
	site := map[domain.Cell]SiteCell{}
	for _, c := range cells {
		site[c.Cell] = c
	}
	free := func(c domain.Cell) bool {
		s, ok := site[c]
		if !ok || !in[c] {
			return false
		}
		walk, wk := s.Walkable.Value()
		if !wk || !walk || s.Occupied() {
			return false
		}
		for dx := int32(-1); dx <= 1; dx++ {
			for dz := int32(-1); dz <= 1; dz++ {
				if n, ok := site[domain.Cell{X: c.X + dx, Z: c.Z + dz}]; ok {
					if door, dk := n.Doorway.Value(); !dk || door {
						return false
					}
				}
			}
		}
		return true
	}
	// walled: a 4-neighbour outside the zone that is not walkable.
	walled := func(c domain.Cell) bool {
		for _, n := range neighbours4(c) {
			if in[n] {
				continue
			}
			s, ok := site[n]
			if !ok {
				continue
			}
			if walk, wk := s.Walkable.Value(); wk && !walk {
				return true
			}
		}
		return false
	}
	type candidate struct {
		piece  InteriorPiece
		walled bool
	}
	var out []candidate
	sorted := append([]domain.Cell(nil), zone...)
	sort.Slice(sorted, func(i, j int) bool {
		return sorted[i].X < sorted[j].X || sorted[i].X == sorted[j].X && sorted[i].Z < sorted[j].Z
	})
	for _, c := range sorted {
		for _, rot := range []domain.Rotation{domain.North, domain.East} {
			other := domain.Cell{X: c.X + 1, Z: c.Z}
			if rot == domain.East {
				other = domain.Cell{X: c.X, Z: c.Z + 1}
			}
			if !free(c) || !free(other) || !shelfLeavesZoneUsable(zone, in, site, c, other) {
				continue
			}
			piece := NewInteriorPiece(fmt.Sprintf("shelf.%d.%d.%s", c.X, c.Z, rot), ShelfDefinition, domain.Cell{X: 2, Z: 1}, rot, c)
			out = append(out, candidate{piece: piece, walled: walled(c) && walled(other)})
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].walled && !out[j].walled })
	pieces := make([]InteriorPiece, 0, min(len(out), maxShelfSites))
	for i := 0; i < len(out) && i < maxShelfSites; i++ {
		pieces = append(pieces, out[i].piece)
	}
	return pieces
}

// shelfLeavesZoneUsable: the zone without a and b is non-empty and
// contiguous, and a and b each touch a remaining walkable zone cell.
func shelfLeavesZoneUsable(zone []domain.Cell, in map[domain.Cell]bool, site map[domain.Cell]SiteCell, a, b domain.Cell) bool {
	rest := map[domain.Cell]bool{}
	for c := range in {
		if c != a && c != b {
			rest[c] = true
		}
	}
	if len(rest) == 0 {
		return false
	}
	for _, shelf := range []domain.Cell{a, b} {
		reached := false
		for _, n := range neighbours4(shelf) {
			if !rest[n] {
				continue
			}
			if walk, wk := site[n].Walkable.Value(); wk && walk {
				reached = true
			}
		}
		if !reached {
			return false
		}
	}
	var start domain.Cell
	for _, c := range zone {
		if rest[c] {
			start = c
			break
		}
	}
	seen := map[domain.Cell]bool{start: true}
	queue := []domain.Cell{start}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, n := range neighbours4(c) {
			if rest[n] && !seen[n] {
				seen[n] = true
				queue = append(queue, n)
			}
		}
	}
	return len(seen) == len(rest)
}

func neighbours4(c domain.Cell) []domain.Cell {
	return []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}}
}
