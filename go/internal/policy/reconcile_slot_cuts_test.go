package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A refused slot's cut wave takes the plants on its footprint and interaction
// cell that the refusal names, passable or not, and nothing else (#2303).
func TestSlotPlantCutsTakeFootprintAndInteractionPlants(t *testing.T) {
	t.Parallel()
	offset := domain.Cell{X: 0, Z: 1}
	piece := InteriorPiece{Def: "Campfire", Size: domain.Cell{X: 1, Z: 1}, Rect: Rectangle{X: 5, Z: 5, Width: 1, Height: 1}, InteractionOffset: &offset}
	interaction, _ := piece.Interaction()
	bush := func(id uint64) Thing { return Thing{ID: id, Def: "Plant_Bush", Category: ThingPlant} }
	cells := []SiteCell{
		{Cell: domain.Cell{X: 5, Z: 5}, Things: []Thing{bush(1)}},
		{Cell: interaction, Things: []Thing{bush(2), {ID: 3, Def: "Plant_Grass", Category: ThingPlant}}},
		{Cell: domain.Cell{X: 9, Z: 9}, Things: []Thing{bush(4)}},
	}
	got := SlotPlantCuts(cells, piece, []PlacementBlocker{{DefName: "Plant_Bush", Category: "Plant"}})
	if len(got) != 2 || got[0].EntityID != bush(1).LoadID() || got[1].EntityID != bush(2).LoadID() {
		t.Fatalf("cuts %+v, want the bushes on the footprint and interaction cells", got)
	}
	if none := SlotPlantCuts(cells, piece, []PlacementBlocker{{DefName: "Wall", Blueprint: true}}); len(none) != 0 {
		t.Fatalf("a wall refusal cut %+v", none)
	}
}
