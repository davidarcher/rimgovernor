package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The power/wind-thin-roof acceptance fixture (PowerFixture "mountain")
// fills the 100x100 lab with thin-roofed granite around a 30x30 pocket at the
// centre, a lamp and conduit line in the pocket's north-west corner. The case
// relies on the layout putting the first turbine on rock beside the pocket,
// its wind path through rock to dig and unroof. Siting may move the turbine a
// few cells (it was (45,34), then (45,35) once siting widened), so the test
// pins that property, not the cell.
func TestThinRoofMountainLabPlansTurbineOnRockBesidePocket(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	plan, ok := DeriveLayoutPlan(survey, 3, TechTierPowered, nil, 0, 0).Value()
	if !ok {
		t.Fatal("no layout plan over the mountain lab")
	}
	sites := PlannedPowerSites(plan, WindTurbineDefinition)
	if len(sites) == 0 {
		t.Fatal("the layout plan reserves no wind turbine site")
	}
	rockIn := func(cells []domain.Cell) (n int) {
		for _, c := range cells {
			if c.X < 0 || c.Z < 0 || c.X >= size || c.Z >= size || survey.Cells[int(c.Z)*size+int(c.X)].Rock {
				n++
			}
		}
		return n
	}
	first := sites[0]
	if rock := rockIn(RectangleCells(first.Area)); rock == 0 {
		t.Fatalf("first turbine %+v holds %d rock cells of %d, want some rock", first, rock, len(RectangleCells(first.Area)))
	}
	if rock := rockIn(TurbineWindCells(first.Cell, first.Rotation)); rock == 0 {
		t.Fatalf("first turbine %+v has no rock on its wind path: nothing for the case to dig", first)
	}
}
