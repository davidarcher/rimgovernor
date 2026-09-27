package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A reported geyser near the core gets its enclosure inside the wall and
// the planned generator on the geyser (#834).
func TestDeriveLayoutPlanGeothermal(t *testing.T) {
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(200, open)
	base, _ := DeriveLayoutPlan(s, 3, nil).Value()
	mid := base.Spine[0].From
	for dz := int32(14); dz <= 40; dz += 2 {
		for _, sign := range []int32{1, -1} {
			at := domain.Cell{X: mid.X, Z: mid.Z + sign*dz}
			g := PowerGeyser{ID: "g", Cell: at, Cells: []domain.Cell{at, {X: at.X + 1, Z: at.Z}, {X: at.X, Z: at.Z + 1}, {X: at.X + 1, Z: at.Z + 1}}}
			plan, ok := DeriveLayoutPlan(s, 3, []PowerGeyser{g}).Value()
			if !ok {
				t.Fatal("no plan")
			}
			var enclosure Rectangle
			var ring Rectangle
			for _, r := range plan.Reservations {
				switch r.Kind {
				case ReserveGeothermal:
					enclosure = r.Area
				case ReservePerimeter:
					ring = unionRect(ring, r.Area)
				}
			}
			if enclosure.Width == 0 {
				continue
			}
			if !plan.Valid() {
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
			sections, err := PerimeterSections(plan, "Wall", "Door")
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
