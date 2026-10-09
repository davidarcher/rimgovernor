package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A campfire with an interaction cell takes a slot whose cell stays on the
// floor, so the ring's walls never block it.
func TestShelterCampfireInteractionCellStaysInside(t *testing.T) {
	shapes := PieceShapes{Defs: map[string]InteriorPieceDef{}, Furniture: testShapes.Furniture}
	for name, def := range testShapes.Defs {
		shapes.Defs[name] = def
	}
	fire := shapes.Defs["Campfire"]
	front := domain.Cell{X: 0, Z: -1}
	fire.Interaction = &front
	shapes.Defs["Campfire"] = fire
	for _, size := range [][2]int32{{6, 4}, {7, 5}, {5, 4}} {
		room := shelterInterior(size[0], size[1], 2)
		room.Shapes = shapes
		plan, ok := PlanInterior(room, InteriorPieceDef{})
		if !ok {
			t.Fatalf("%v: no plan", size)
		}
		fires := slotsOf(plan, "campfire.")
		if len(fires) != ShelterCampfires(true) {
			t.Fatalf("%v: %d campfire slots", size, len(fires))
		}
		for _, p := range fires {
			if c, ok := p.Interaction(); !ok || !rectContains(room.Interior, c) {
				t.Fatalf("%v: campfire %+v interaction %v leaves the interior", size, p, c)
			}
		}
	}
}
