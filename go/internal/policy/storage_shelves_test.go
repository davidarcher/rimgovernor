package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// shelfRoom is a walled room whose interior (x, z in [1, w] x [1, h]) is
// one zone; door marks one doorway cell in the wall.
func shelfRoom(w, h int32, door domain.Cell) ([]domain.Cell, []SiteCell) {
	var zone []domain.Cell
	var cells []SiteCell
	for x := int32(0); x <= w+1; x++ {
		for z := int32(0); z <= h+1; z++ {
			c := domain.Cell{X: x, Z: z}
			wall := x == 0 || z == 0 || x == w+1 || z == h+1
			doorway := c == door
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(!wall || doorway), Occupied: domain.Known(wall), Doorway: domain.Known(doorway)})
			if !wall {
				zone = append(zone, c)
			}
		}
	}
	return zone, cells
}

func TestShelfSitesPreferTheWallAwayFromTheDoor(t *testing.T) {
	t.Parallel()
	zone, cells := shelfRoom(5, 5, domain.Cell{X: 3, Z: 0})
	pieces := ShelfSites(zone, cells)
	if len(pieces) == 0 || len(pieces) > maxShelfSites {
		t.Fatal(pieces)
	}
	for _, p := range pieces {
		if p.Def != ShelfDefinition {
			t.Fatal(p)
		}
		for x := p.Rect.X; x < p.Rect.X+p.Rect.Width; x++ {
			for z := p.Rect.Z; z < p.Rect.Z+p.Rect.Height; z++ {
				if z <= 1 && x >= 2 && x <= 4 {
					t.Fatal("shelf blocks the doorway", p)
				}
			}
		}
	}
	first := pieces[0].Rect
	if first.X != 1 && first.X+first.Width-1 != 5 && first.Z != 1 && first.Z+first.Height-1 != 5 {
		t.Fatal("first site must back onto a wall", first)
	}
}

func TestShelfSitesKeepASmallZoneContiguousAndReachable(t *testing.T) {
	t.Parallel()
	// A 2x2 working stockpile in the middle of an open floor.
	var zone []domain.Cell
	var cells []SiteCell
	for x := int32(0); x < 6; x++ {
		for z := int32(0); z < 6; z++ {
			c := domain.Cell{X: x, Z: z}
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Doorway: domain.Known(false)})
			if x >= 2 && x <= 3 && z >= 2 && z <= 3 {
				zone = append(zone, c)
			}
		}
	}
	pieces := ShelfSites(zone, cells)
	if len(pieces) != 4 {
		t.Fatal("a 2x2 takes a shelf on any side", pieces)
	}
	// A 1x3 strip: a shelf on the middle would split the zone.
	strip := []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}, {X: 3, Z: 1}}
	for _, p := range ShelfSites(strip, cells) {
		if p.Rect.X == 2 {
			t.Fatal("shelf would split the zone", p)
		}
	}
	// Two cells cannot hold a shelf and a zone.
	if got := ShelfSites(strip[:2], cells); len(got) != 0 {
		t.Fatal(got)
	}
}

func TestNextShelfStepBuildsOneAtATime(t *testing.T) {
	t.Parallel()
	zone, cells := shelfRoom(5, 5, domain.Cell{X: 3, Z: 0})
	z := ShelfZone{Zone: "Zone_1", Cells: zone, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority}
	request := ShelfRequest{Zones: []ShelfZone{z}, Cells: cells}
	if got := NextShelfStep(request); got.Kind != ShelfNone {
		t.Fatal("unresearched shelves must not be built", got)
	}
	request.Available = true
	if got := NextShelfStep(request); got.Kind != ShelfBuild || got.Zone.Zone != "Zone_1" || len(got.Pieces) == 0 {
		t.Fatal(got)
	}
	built := ShelfRecord{Zone: "Zone_1", Building: "Shelf_9", Cells: []domain.Cell{{X: 1, Z: 5}, {X: 2, Z: 5}}}
	request.Shelves = []ShelfRecord{built}
	if got := NextShelfStep(request); got.Kind != ShelfBuild {
		t.Fatal("a built shelf under quota holds nothing", got)
	}
	built.Open = true
	request.Shelves = []ShelfRecord{built}
	if got := NextShelfStep(request); got.Kind != ShelfNone {
		t.Fatal("an open shelf holds the next", got)
	}
	// 25 zone cells + 10 shelf cells: a third is 10 cells, five shelves.
	built.Open = false
	request.Shelves = []ShelfRecord{built, built, built, built, built}
	if got := NextShelfStep(request); got.Kind != ShelfNone {
		t.Fatal("quota reached", got)
	}
}

func TestShelfQuotaIsAThirdInWholeShelves(t *testing.T) {
	t.Parallel()
	if ShelfQuotaCells(4) != 2 || ShelfQuotaCells(3) != 0 || ShelfQuotaCells(25) != 8 {
		t.Fatal(ShelfQuotaCells(4), ShelfQuotaCells(25))
	}
}
