package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// A reported geyser near the core gets its enclosure inside the wall and
// the planned generator on the geyser.
func TestDeriveLayoutPlanGeothermal(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(200, open)
	base, _ := derivePlan(s, 3, nil)
	ext, _ := base.CoreBounds()
	mid := domain.Cell{X: ext.X + ext.Width/2, Z: ext.Z + ext.Height/2}
	for dz := int32(14); dz <= 40; dz += 2 {
		for _, sign := range []int32{1, -1} {
			at := domain.Cell{X: mid.X, Z: mid.Z + sign*dz}
			g := PowerGeyser{ID: "g", Cell: at, Cells: []domain.Cell{at, {X: at.X + 1, Z: at.Z}, {X: at.X, Z: at.Z + 1}, {X: at.X + 1, Z: at.Z + 1}}}
			plan, ok := derivePlan(s, 3, []PowerGeyser{g})
			if !ok {
				t.Fatal("no plan")
			}
			var enclosure Rectangle
			var ring Rectangle
			for _, r := range plan.Reservations {
				if r.Kind == ReserveGeothermal {
					enclosure = r.Area.Bounds()
				}
			}
			if node, ok := plan.Ring(); ok {
				ring = node.Footprint.Bounds()
			}
			// The wall never enters the edge margin, so a geyser
			// the site leaves that near the edge stays outside it.
			m := LayoutEdgeMargin + 2*perimeterThick
			wallable := Rectangle{X: m, Z: m, Width: 200 - 2*m, Height: 200 - 2*m}
			if enclosure.Width == 0 || unionRect(wallable, enclosure) != wallable {
				continue
			}
			if !plan.WithRoomIDs().Valid() {
				t.Fatal("invalid plan with geothermal")
			}
			inner := pad(ring, -perimeterThick)
			if unionRect(inner, enclosure) != inner {
				t.Fatalf("enclosure %+v not inside the wall %+v", enclosure, inner)
			}
			sites := PlannedPowerSites(plan, GeothermalDefinition)
			if len(sites) != 1 || sites[0].Cell != at || sites[0].Area != pad(enclosure, -geothermalShell) {
				t.Fatalf("geothermal site %+v, geyser at %v", sites, at)
			}
			sections, err := PerimeterSections(plan, "Wall", "Door", PerimeterBridge)
			if err != nil {
				t.Fatal(err)
			}
			found := false
			for _, sec := range sections {
				found = found || sec.Name == TierGeothermal
			}
			if !found {
				t.Fatal("no perimeter-geothermal section")
			}
			return
		}
	}
	t.Fatal("no geyser offset got a geothermal reservation")
}
