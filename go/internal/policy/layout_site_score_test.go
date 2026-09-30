package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// richUnderRooms counts the rich cells under p's rooms.
func richUnderRooms(g coreGrid, p LayoutPlan) int {
	n := 0
	for _, r := range p.AllRooms() {
		for _, c := range rectCells(r.Interior) {
			if g.soil[c] == soilCostRich {
				n++
			}
		}
	}
	return n
}

// centreRichSurvey: a rich patch at the map centre, plain soil around it.
func centreRichSurvey() MapSurvey {
	return zoningSurvey(140, func(x, z int32) SurveyCell {
		if x >= 50 && x < 90 && z >= 50 && z < 90 {
			return SurveyCell{Walkable: true, Fertility: 1.4}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
}

func TestSiteCoreLandsOffCentreRichPatch(t *testing.T) {
	s := centreRichSurvey()
	zones := Zone(s)
	g := newCoreGrid(zones, nil).withSoil(s)
	centroid := PlanCore(zones, 3, BuildTierCamp)
	sited := SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp)
	if len(sited.Rooms) == 0 {
		t.Fatal("no rooms")
	}
	if c, r := richUnderRooms(g, centroid), richUnderRooms(g, sited); r != 0 || c == 0 {
		t.Fatal("rich cells under rooms: centroid", c, "sited", r)
	}
	if !reflect.DeepEqual(sited, SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp)) {
		t.Fatal("same input gave a different plan")
	}
}

func TestSiteCoreBaselineNoRicherThanCentroid(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	zones := Zone(s)
	g := newCoreGrid(zones, nil).withSoil(s)
	centroid := PlanCore(zones, 3, BuildTierCamp)
	sited := SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp)
	if c, r := richUnderRooms(g, centroid), richUnderRooms(g, sited); r > c {
		t.Fatal("rich cells under rooms: centroid", c, "sited", r)
	}
	if g.scoreSite(sited) < g.scoreSite(centroid) {
		t.Fatal("sited plan scores below the centroid plan")
	}
	if !reflect.DeepEqual(sited, SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp)) {
		t.Fatal("same input gave a different plan")
	}
}

// westRichSurvey: barren ground with one small rich patch west of centre.
func westRichSurvey() MapSurvey {
	return zoningSurvey(160, func(x, z int32) SurveyCell {
		if x >= 30 && x < 40 && z >= 75 && z < 85 {
			return SurveyCell{Walkable: true, Fertility: 1.4}
		}
		return SurveyCell{Walkable: true}
	})
}

// TestSiteWallScoreFavoursEnclosedRichPatch (#1288): two cores on
// barren ground, one beside a small rich patch its wall takes in; that
// site's wall score wins, and the sited plan's wall encloses the patch.
func TestSiteWallScoreFavoursEnclosedRichPatch(t *testing.T) {
	s := westRichSurvey()
	zones := Zone(s)
	g := newCoreGrid(zones, nil).withSoil(s)
	ground := newSiteGround(s)
	grow := func(x int32) LayoutPlan {
		seed := domain.Cell{X: x, Z: 80}
		return Grow(LayoutPlan{Zones: zones, Spine: []SpineSegment{{From: seed, To: seed}}}, 3, 1, BuildTierCamp)
	}
	near, far := grow(55), grow(120)
	if len(near.Rooms) == 0 || len(far.Rooms) == 0 {
		t.Fatal("no rooms")
	}
	wn, wf := g.scoreWall(PlanPerimeter(near, s), ground), g.scoreWall(PlanPerimeter(far, s), ground)
	if wn <= wf {
		t.Fatal("wall score near the rich patch", wn, "far from it", wf)
	}
	sited := PlanPerimeter(SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp), s)
	bare := g
	bare.soil = map[domain.Cell]int{}
	if g.scoreWall(sited, ground)-bare.scoreWall(sited, ground) < siteEnclosedWeight*soilCostRich*100 {
		t.Fatal("sited wall does not take in the rich patch")
	}
}

// TestSoilCostBaselineFixture is soilCost on the real #1280 survey: every
// rich cell costs soilCostRich and rock costs nothing.
func TestSoilCostBaselineFixture(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	g := newCoreGrid(Zone(s), nil).withSoil(s)
	rich, rock := 0, 0
	for _, c := range s.Cells {
		r := Rectangle{X: c.Cell.X, Z: c.Cell.Z, Width: 1, Height: 1}
		switch {
		case c.Rock:
			if g.soilCost(r) != soilCostOther {
				t.Fatal("rock cell costs", c.Cell, g.soilCost(r))
			}
			rock++
		case c.Fertility > zoneRichFertility:
			if g.soilCost(r) != soilCostRich {
				t.Fatal("rich cell costs", c.Cell, g.soilCost(r))
			}
			rich++
		}
	}
	whole := g.soilCost(Rectangle{Width: s.Bounds.Width, Height: s.Bounds.Height})
	if rich == 0 || rock == 0 || whole < rich*soilCostRich {
		t.Fatal("fixture soil cost", rich, rock, whole)
	}
}
