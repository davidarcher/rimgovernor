package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The power/wind-thin-roof acceptance fixture (PowerFixture "mountain")
// fills the 100x100 lab with thin-roofed granite around a 30x30 pocket at the
// centre, a lamp and conduit line in the pocket's north-west corner. The
// tiled plan sites the turbines from site nodes (#2960): rock is no blocker
// to a wind lane (the game counts only roof, buildings and the map edge), so
// a turbine stands wherever its lane loses least, and the plan no longer seeks
// rock to dig. The test pins that the plan holds the first turbines on the
// map, none of them losing all output to blocked lane cells.
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
	fact, _, _ := DeriveLayoutPlan(survey, colonyStationFacts(3, survey), nil, 0)
	plan, ok := fact.Value()
	if !ok {
		t.Fatal("no layout plan over the mountain lab")
	}
	if sites := PlannedPowerSites(plan, WindTurbineDefinition); len(sites) == 0 {
		t.Fatal("the layout plan holds no wind turbine site")
	}
	for _, s := range plan.LiveSites(NodeTurbine) {
		for _, c := range s.Footprint.Cells() {
			if c.X < 0 || c.Z < 0 || c.X >= size || c.Z >= size {
				t.Fatalf("turbine %s stands off the map at %v", s.Node, c)
			}
		}
		for _, l := range s.Lanes {
			if l.Blocked >= 5 {
				t.Errorf("turbine %s lane loses all output: %d blocked", s.Node, l.Blocked)
			}
		}
	}
}
