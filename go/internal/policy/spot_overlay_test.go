package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSpotOverlayLabelsStandingAndPlannedSpots(t *testing.T) {
	spot, _ := domain.NewBuilding("ButcherSpot", domain.Cell{X: 5, Z: 6}, domain.North, "")
	craft, _ := domain.NewBuilding("CraftingSpot", domain.Cell{X: 9, Z: 9}, domain.North, "")
	wall, _ := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "WoodLog")
	got := SpotOverlay(CurrentConstruction{
		Buildings: []CurrentBuilding{{Building: spot, Cells: []domain.Cell{{X: 5, Z: 6}}}, {Building: wall}},
		Sites:     []ConstructionSite{{Building: craft}},
	}, Bounds{Width: 20, Height: 20})
	labels := map[string]domain.Cell{}
	for _, l := range got.Labels {
		labels[l.Text] = l.Cell
	}
	if len(labels) != 2 || labels["butcher spot"] != (domain.Cell{X: 5, Z: 6}) || labels["crafting spot (planned)"] != (domain.Cell{X: 9, Z: 9}) {
		t.Fatalf("labels %v", labels)
	}
	if empty := SpotOverlay(CurrentConstruction{Buildings: []CurrentBuilding{{Building: wall}}}, Bounds{Width: 20, Height: 20}); len(empty.Layers) != 0 {
		t.Fatalf("a wall drew %+v", empty)
	}
}
