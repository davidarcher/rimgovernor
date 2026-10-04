package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// A reported geyser near the core gets its enclosure inside the wall and
// the planned generator on the geyser (#834).
func TestDeriveLayoutPlanGeothermal(t *testing.T) {
	t.Skip("fails below the production seed count: #2004")
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(200, open)
	base, _ := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30).Value()
	mid := base.Spine[0].From
	for dz := int32(14); dz <= 40; dz += 2 {
		for _, sign := range []int32{1, -1} {
			at := domain.Cell{X: mid.X, Z: mid.Z + sign*dz}
			g := PowerGeyser{ID: "g", Cell: at, Cells: []domain.Cell{at, {X: at.X + 1, Z: at.Z}, {X: at.X, Z: at.Z + 1}, {X: at.X + 1, Z: at.Z + 1}}}
			plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, []PowerGeyser{g}, 30).Value()
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
			// The wall never enters the edge margin (#1279), so a geyser
			// the site leaves that near the edge stays outside it.
			m := LayoutEdgeMargin + 2*perimeterThick
			wallable := Rectangle{X: m, Z: m, Width: 200 - 2*m, Height: 200 - 2*m}
			if enclosure.Width == 0 || unionRect(wallable, enclosure) != wallable {
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
			sections, err := PerimeterSections(plan, "Wall", "Door", PerimeterBridge, nil)
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

// No planned room covers a geyser's enclosure, on a fresh plan or on a
// replan of a plan laid out before the geyser was known.
func TestLayoutRoomsKeepOffGeysers(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(200, open)
	base, _ := DeriveLayoutPlan(s, 5, BuildTierCamp, nil, 30).Value()
	for _, r := range base.AllRooms() {
		at := domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}
		g := []PowerGeyser{{ID: "g", Cell: at, Cells: []domain.Cell{at, {X: at.X + 1, Z: at.Z}, {X: at.X, Z: at.Z + 1}, {X: at.X + 1, Z: at.Z + 1}}}}
		vents := geothermalCells(geyserFootprints(g))
		plan, ok := DeriveLayoutPlan(s, 5, BuildTierCamp, g, 30).Value()
		if !ok {
			t.Fatalf("no plan with a geyser under %s", r.Role)
		}
		for _, room := range plan.AllRooms() {
			if rectHits(roomWalls(room), vents) {
				t.Fatalf("fresh plan: %s covers the geyser at %v", room.Role, at)
			}
		}
		next, changed := replanTest(base, s, 5, 1, BuildTierCamp, g, nil)
		if !changed {
			t.Fatalf("replan kept %s over the geyser at %v", r.Role, at)
		}
		for _, room := range next.AllRooms() {
			if rectHits(roomWalls(room), vents) {
				t.Fatalf("replan: %s covers the geyser at %v", room.Role, at)
			}
		}
	}
}
