package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoomGrowth.Fixed reads one helper: the
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

// A site or claim marks its whole footprint when the definition's
// size was read, not only its anchor.
func TestOccupiedCellsMarkSiteFootprint(t *testing.T) {
	bed, err := domain.NewBuilding("Bed", domain.Cell{X: 10, Z: 10}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	claimed, err := domain.NewBuilding("Bed", domain.Cell{X: 20, Z: 10}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	var p observation.ColonyProjection
	p.Definitions = []observation.PlanningDefinition{{Name: "Bed", Size: domain.Known(policy.Bounds{Width: 1, Height: 2})}}
	p.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{
		Colony: true,
		Sites:  []policy.ConstructionSite{{Building: bed, Stage: "blueprint"}, {Building: claimed, Stage: "frame"}},
	})
	p.Facts.ConstructionClaims = domain.Known([]policy.ConstructionClaim{{Building: claimed}})
	cells, ok := occupiedCells(p)
	if !ok {
		t.Fatal("census known but cells unknown")
	}
	for _, b := range []domain.Building{bed, claimed} {
		rect := policy.OccupiedRect(b.Cell(), domain.Cell{X: 1, Z: 2}, b.Rotation())
		if rect.Width*rect.Height != 2 {
			t.Fatalf("footprint %v", rect)
		}
		for _, c := range policy.RectangleCells(rect) {
			if !cells[c] {
				t.Fatalf("footprint cell %v of %v missing: %v", c, b.Cell(), cells)
			}
		}
	}
	if len(cells) != 4 {
		t.Fatalf("cells = %v", cells)
	}
}

// The open plans keyed by a planned room's origin make that room
// fixed; a retired plan or another method does not.
func TestRoomMethodOrigins(t *testing.T) {
	plans := []store.PlanState{
		{Method: "kitchen-shell-5-6"},
		{Method: "plan-dig-room-workshop-7-8"},
		{Method: "bedroom-furnish-9-10"},
		{Method: "tomb-place-11-12-sarcophagus"},
		{Method: "kitchen-shell-1-1", Retired: true},
		{Method: "maintain-food-2-3"},
		{Method: ""},
	}
	got := roomMethodOrigins(plans)
	want := map[domain.Cell]bool{{X: 5, Z: 6}: true, {X: 7, Z: 8}: true, {X: 9, Z: 10}: true, {X: 11, Z: 12}: true}
	if len(got) != len(want) {
		t.Fatalf("origins = %v", got)
	}
	for c := range want {
		if !got[c] {
			t.Fatalf("origins = %v, missing %v", got, c)
		}
	}
}
