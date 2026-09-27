package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func foodBlockCells(x0, z0, w, h int32, roofed bool) map[domain.Cell]policy.SiteCell {
	cells := map[domain.Cell]policy.SiteCell{}
	for x := x0; x < x0+w; x++ {
		for z := z0; z < z0+h; z++ {
			c := domain.Cell{X: x, Z: z}
			cells[c] = policy.SiteCell{Cell: c, Indoors: domain.Known(roofed), Roofed: domain.Known(roofed), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)}
		}
	}
	return cells
}

// A roofed room that is not a bedroom wins over the sleeping room, and the
// sleeping room still serves when it is the only roofed floor.
func TestFoodStorageBlocksPreferNonBedroomRoofedFloor(t *testing.T) {
	cells := foodBlockCells(0, 0, 3, 3, true)
	for c, v := range foodBlockCells(10, 0, 3, 3, true) {
		cells[c] = v
	}
	sleeping := map[domain.Cell]bool{}
	for c := range foodBlockCells(0, 0, 3, 3, true) {
		sleeping[c] = true
	}
	sites := foodStorageBlocks(cells, nil, domain.Cell{}, func(c policy.SiteCell) bool { return roofedIndoors(c) && !sleeping[c.Cell] })
	if len(sites) != 1 || sites[0][0] != (domain.Cell{X: 10, Z: 0}) {
		t.Fatalf("non-bedroom: %v", sites)
	}
	sites = foodStorageBlocks(cells, nil, domain.Cell{}, roofedIndoors)
	if len(sites) != 2 || sites[0][0] != (domain.Cell{X: 0, Z: 0}) {
		t.Fatalf("any roofed, nearest first: %v", sites)
	}
}

// Before any roof, the outdoor tier finds the block nearest the cooking
// spot; the roofed tiers find nothing.
func TestFoodStorageBlocksOutdoorNearAnchor(t *testing.T) {
	cells := foodBlockCells(0, 0, 20, 20, false)
	if sites := foodStorageBlocks(cells, nil, domain.Cell{X: 10, Z: 10}, roofedIndoors); len(sites) != 0 {
		t.Fatalf("roofed tier outdoors: %v", sites)
	}
	sites := foodStorageBlocks(cells, map[domain.Cell]bool{{X: 10, Z: 10}: true}, domain.Cell{X: 10, Z: 10}, func(policy.SiteCell) bool { return true })
	if len(sites) != maxFoodStorageSites {
		t.Fatalf("bounded: %d", len(sites))
	}
	for _, block := range sites {
		for _, c := range block {
			if c == (domain.Cell{X: 10, Z: 10}) {
				t.Fatalf("occupied cell used: %v", block)
			}
		}
		if d := (block[4].X-10)*(block[4].X-10) + (block[4].Z-10)*(block[4].Z-10); d > 8 {
			t.Fatalf("far from anchor: %v", block)
		}
	}
}

func TestFoodStorageAnchorPrefersCookingBench(t *testing.T) {
	room := policy.Rectangle{X: 0, Z: 0, Width: 9, Height: 9}
	if c, ok := foodStorageAnchor(nil, room, true); !ok || c != (domain.Cell{X: 4, Z: 4}) {
		t.Fatal(c, ok)
	}
	if _, ok := foodStorageAnchor(nil, room, false); ok {
		t.Fatal("anchored with nothing")
	}
}
