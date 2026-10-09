package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The incinerator interior: bare ground that grows plants (soil) or
// burns gets a non-flammable floor; rock floor needs none. Fertility and
// flammability come from the terrain stats, not def names.
func TestFlooringIncineratorTier(t *testing.T) {
	terrains := flooringTerrains()
	terrains["Soil"] = FloorTerrain{Cleanliness: -1, PathCost: 2, Fertility: 1, Natural: true}
	terrains["GraniteRough"] = FloorTerrain{PathCost: 1, Natural: true}
	review := func(terrain string) FlooringReview {
		var cells []FloorCell
		for z := int32(1); z <= 3; z++ {
			for x := int32(1); x <= 3; x++ {
				cells = append(cells, FloorCell{Cell: domain.Cell{X: x, Z: z}, Terrain: terrain})
			}
		}
		r, err := ReviewFlooring(domain.Known(FlooringObservation{Terrains: terrains, Incinerator: cells}), domain.Unknown[RoomObservation](), nil, flooringPolicy())
		if err != nil {
			t.Fatal(err)
		}
		return r
	}
	r := review("Soil")
	if len(r.Deficits) != 1 || r.Deficits[0].Tier != FloorTierIncinerator || len(r.Deficits[0].Cells) != 9 {
		t.Fatal(r)
	}
	got, err := SelectFlooringMethod(r, firebreakFlooringFacts(100, 100), flooringPolicy())
	if err != nil || got.Method != FlooringBuild || got.Definition != "Concrete" || got.Tier != FloorTierIncinerator || len(got.Cells) != 9 {
		t.Fatal(got, err)
	}
	for _, terrain := range []string{"GraniteRough", "Concrete"} {
		if r := review(terrain); r.Active {
			t.Fatal(terrain, r)
		}
	}
	if r := review("WoodPlankFloor"); len(r.Deficits) != 1 {
		t.Fatal("flammable floor must be replaced", r)
	}
}
