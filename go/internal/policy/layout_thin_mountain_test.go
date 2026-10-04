package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The power/wind-thin-roof acceptance fixture (PowerFixture "mountain", #1873)
// fills the 100x100 lab with thin-roofed granite around a 30x30 pocket at the
// centre, a lamp and conduit line in the pocket's north-west corner. The case
// relies on the layout putting the first turbine on rock beside the pocket:
// centre (45,34) facing east, its footprint's southern six cells in the pocket.
func TestThinRoofMountainLabPlansTurbineOnRockBesidePocket(t *testing.T) {
	t.Parallel()
	const size, pocketMin, pocketMax = 100, 35, 65
	var cells []SurveyCell
	for z := int32(0); z < size; z++ {
		for x := int32(0); x < size; x++ {
			c := SurveyCell{Cell: domain.Cell{X: x, Z: z}, Footing: FootingFirm}
			if x >= pocketMin && x < pocketMax && z >= pocketMin && z < pocketMax {
				c.Walkable = true
				c.Prop = z == 36 && x <= 38 || x == 38 && z == 37 // conduit line and lamp
			} else {
				c.Rock = true
			}
			cells = append(cells, c)
		}
	}
	survey := MapSurvey{Bounds: Bounds{Width: size, Height: size}, Cells: cells}
	plan, ok := DeriveLayoutPlan(survey, 3, BuildTierPowered, nil, 0).Value()
	if !ok {
		t.Fatal("no layout plan over the mountain lab")
	}
	sites := PlannedPowerSites(plan, WindTurbineDefinition)
	if len(sites) == 0 || sites[0].Cell != (domain.Cell{X: 45, Z: 34}) || sites[0].Rotation != domain.East {
		t.Fatalf("first turbine site = %+v, want centre (45,34) facing east", sites)
	}
	rock := 0
	for _, c := range RectangleCells(sites[0].Area) {
		if survey.Cells[int(c.Z)*size+int(c.X)].Rock {
			rock++
		}
	}
	if rock != 8 {
		t.Fatalf("turbine footprint holds %d rock cells, want 8 (the rest in the pocket)", rock)
	}
}
