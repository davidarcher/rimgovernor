package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func coveredSiteCell(x, z int32, walkable, occupied, zone, roofed, storageEmpty bool) SiteCell {
	return SiteCell{
		Cell:         domain.Cell{X: x, Z: z},
		Walkable:     domain.Known(walkable),
		Occupied:     domain.Known(occupied),
		Zone:         domain.Known(zone),
		Roofed:       domain.Known(roofed),
		StorageEmpty: domain.Known(storageEmpty),
	}
}

func coveredStorageGrid(width, height int32, fn func(x, z int32) SiteCell) []SiteCell {
	var cells []SiteCell
	for x := int32(0); x < width; x++ {
		for z := int32(0); z < height; z++ {
			cells = append(cells, fn(x, z))
		}
	}
	return cells
}

func TestCoveredStorageSitesPrefersNearestLegalPatch(t *testing.T) {
	cells := coveredStorageGrid(10, 10, func(x, z int32) SiteCell {
		return coveredSiteCell(x, z, true, false, false, true, true)
	})
	sites, err := CoveredStorageSites(CoveredStorageRequest{Bounds: Bounds{Width: 10, Height: 10}, Anchor: domain.Cell{X: 5, Z: 5}, Cells: cells})
	if err != nil {
		t.Fatal(err)
	}
	if len(sites) == 0 {
		t.Fatal("expected at least one site")
	}
	first := sites[0]
	if first.Width != 2 || first.Height != 2 {
		t.Fatalf("unexpected footprint: %+v", first)
	}
	// The nearest legal 2x2 anchored corner to (5,5) among integer corners is (4,4) or (5,5)-adjacent.
	dist := func(r Rectangle) int64 {
		return squaredDistance(domain.Cell{X: r.X, Z: r.Z}, domain.Cell{X: 5, Z: 5})
	}
	for _, s := range sites {
		if dist(s) < dist(first) {
			t.Fatalf("sites not sorted by distance: %+v before %+v", first, s)
		}
	}
}

func TestCoveredStorageSitesRejectsUnroofedOrOccupiedOrNonEmpty(t *testing.T) {
	cells := coveredStorageGrid(4, 4, func(x, z int32) SiteCell {
		switch {
		case x == 0 && z == 0:
			return coveredSiteCell(x, z, true, false, false, false, true) // not roofed
		case x == 2 && z == 0:
			return coveredSiteCell(x, z, true, true, false, true, true) // occupied
		case x == 0 && z == 2:
			return coveredSiteCell(x, z, true, false, false, true, false) // storage not empty
		default:
			return coveredSiteCell(x, z, true, false, false, true, true)
		}
	})
	sites, err := CoveredStorageSites(CoveredStorageRequest{Bounds: Bounds{Width: 4, Height: 4}, Anchor: domain.Cell{X: 0, Z: 0}, Cells: cells})
	if err != nil {
		t.Fatal(err)
	}
	for _, s := range sites {
		blocked := map[domain.Cell]bool{{X: 0, Z: 0}: true, {X: 2, Z: 0}: true, {X: 0, Z: 2}: true}
		for x := s.X; x < s.X+s.Width; x++ {
			for z := s.Z; z < s.Z+s.Height; z++ {
				if blocked[domain.Cell{X: x, Z: z}] {
					t.Fatalf("site %+v overlaps illegal cell (%d,%d)", s, x, z)
				}
			}
		}
	}
}

func TestCoveredStorageSitesExcludesProtectedCells(t *testing.T) {
	cells := coveredStorageGrid(4, 4, func(x, z int32) SiteCell {
		return coveredSiteCell(x, z, true, false, false, true, true)
	})
	sites, err := CoveredStorageSites(CoveredStorageRequest{Bounds: Bounds{Width: 4, Height: 4}, Anchor: domain.Cell{X: 0, Z: 0}, Cells: cells, Protected: []domain.Cell{{X: 0, Z: 0}}})
	if err != nil {
		t.Fatal(err)
	}
	for _, s := range sites {
		if s.X == 0 && s.Z == 0 {
			t.Fatalf("expected protected cell to exclude the (0,0) patch, got %+v", sites)
		}
	}
}

func TestCoveredStorageSitesCapsAtEight(t *testing.T) {
	cells := coveredStorageGrid(40, 40, func(x, z int32) SiteCell {
		return coveredSiteCell(x, z, true, false, false, true, true)
	})
	sites, err := CoveredStorageSites(CoveredStorageRequest{Bounds: Bounds{Width: 40, Height: 40}, Anchor: domain.Cell{X: 20, Z: 20}, Cells: cells})
	if err != nil {
		t.Fatal(err)
	}
	if len(sites) != 8 {
		t.Fatalf("expected 8 candidate sites, got %d", len(sites))
	}
}

func TestCoveredStorageSitesRejectsInvalidBounds(t *testing.T) {
	if _, err := CoveredStorageSites(CoveredStorageRequest{Bounds: Bounds{Width: 0, Height: 4}}); err == nil {
		t.Fatal("expected error")
	}
	if _, err := CoveredStorageSites(CoveredStorageRequest{Bounds: Bounds{Width: 4, Height: 4}, Anchor: domain.Cell{X: 9, Z: 0}}); err == nil {
		t.Fatal("expected out of bounds anchor error")
	}
}
