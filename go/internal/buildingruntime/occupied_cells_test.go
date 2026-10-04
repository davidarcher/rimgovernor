package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// #1943: ReplanFresh's built set and RoomGrowth.Fixed share one helper: the
// census, its blueprint and frame sites and the open journal claims count; a
// claim whose work closed without a building does not.
func TestOccupiedCellsCountSitesAndOpenClaims(t *testing.T) {
	wall := func(x int32) domain.Building {
		b, err := domain.NewBuilding("Wall", domain.Cell{X: x, Z: 5}, domain.North, "BlocksGranite")
		if err != nil {
			t.Fatal(err)
		}
		return b
	}
	built, site, claimed, gone := wall(1), wall(2), wall(3), wall(4)
	var p observation.ColonyProjection
	p.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{
		Colony:    true,
		Buildings: []policy.CurrentBuilding{{ID: "1", Building: built, Cells: []domain.Cell{built.Cell()}}},
		Sites:     []policy.ConstructionSite{{Building: site, Stage: "blueprint"}, {Building: claimed, Stage: "frame"}},
	})
	p.Facts.ConstructionClaims = domain.Known([]policy.ConstructionClaim{{Building: claimed}, {Building: gone}})
	cells, ok := occupiedCells(p)
	if !ok {
		t.Fatal("census known but cells unknown")
	}
	for _, b := range []domain.Building{built, site, claimed} {
		if !cells[b.Cell()] {
			t.Fatalf("cell %v missing", b.Cell())
		}
	}
	if cells[gone.Cell()] || len(cells) != 3 {
		t.Fatalf("cells = %v", cells)
	}
}
