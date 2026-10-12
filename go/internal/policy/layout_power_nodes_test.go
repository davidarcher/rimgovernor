package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWindOutputIsTwentyPercentPerBlockedCell(t *testing.T) {
	for blocked, want := range map[int]float64{0: 1, 1: 0.8, 2: 0.6, 4: 0.2, 5: 0, 9: 0} {
		if got := WindOutput(blocked); got < want-1e-9 || got > want+1e-9 {
			t.Errorf("WindOutput(%d) = %v, want %v", blocked, got, want)
		}
	}
}

// Every turn of a turbine node carries the game's wind path of its centre,
// which holds none of the turbine's own cells: it never blocks itself.
func TestTurbineNodeLanesAreTheGamesPath(t *testing.T) {
	n := turbineNode("wind-1")
	shapes := append([]SiteNode{n}, n.Alts...)
	if len(shapes) != 4 {
		t.Fatalf("%d turns", len(shapes))
	}
	for _, s := range shapes {
		if len(s.Lanes) != 1 || !s.Lanes[0].Wind {
			t.Fatalf("lanes %+v", s.Lanes)
		}
		centre, rot := TurbinePlacement(s.Footprint.Bounds(), s.Rotation == domain.North || s.Rotation == domain.East)
		if rot != s.Rotation {
			t.Errorf("turn %s places as %s", s.Rotation, rot)
		}
		want := RegionOf(TurbineWindCells(centre, rot))
		if !s.Lanes[0].Region.Equal(want) || want.Area() != 112 || s.Lanes[0].Region.Overlaps(s.Footprint) {
			t.Errorf("%s: lane %d cells, overlaps footprint %v", s.Rotation, s.Lanes[0].Region.Area(), s.Lanes[0].Region.Overlaps(s.Footprint))
		}
	}
}

// The ground charges a lane only for what the game counts: off the map, thick
// roof, a fenced room's cells and another footprint. A field costs nothing.
func TestWindGroundPricesOnlyBlockers(t *testing.T) {
	in := siteInput(t)
	in.Embed.Survey.Cells = append(slices.Clone(in.Embed.Survey.Cells), SurveyCell{Cell: domain.Cell{X: 12, Z: 12}, ThickRoof: true})
	g := newWindGround(in)
	lane := RectRegion(Rectangle{X: 10, Z: 10, Width: 4, Height: 4})
	field := false
	for _, z := range in.Embed.Zones {
		field = field || z.Kind == ZoneField && RegionOfRuns(z.Runs).Overlaps(lane)
	}
	roofed := g.blocked(lane, Region{})
	if roofed != 1 {
		t.Errorf("one thick-roof cell in the lane: blocked %d (lane over a field: %v)", roofed, field)
	}
	other := RectRegion(Rectangle{X: 10, Z: 10, Width: 2, Height: 1})
	g.add(other)
	if got := g.blocked(lane, Region{}); got != 3 {
		t.Errorf("another footprint in the lane: blocked %d, want 3", got)
	}
	if got := g.blocked(lane, other); got != 1 {
		t.Errorf("a node's own footprint is no blocker: blocked %d, want 1", got)
	}
	off := RectRegion(Rectangle{X: -2, Z: 0, Width: 3, Height: 1})
	if got := g.blocked(off, Region{}); got != 2 {
		t.Errorf("off the map: blocked %d, want 2", got)
	}
}

// Turbines pick the turn and place of least lost output; with ground to spare
// none loses any, and the run is deterministic.
func TestEmbedSitesPlacesTurbinesWithoutLoss(t *testing.T) {
	in := siteInput(t, turbineNode("wind-1"), turbineNode("wind-2"))
	got, err := EmbedSites(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Sites) != 2 {
		t.Fatalf("sites %+v", got.Decisions)
	}
	for _, p := range got.Sites {
		if p.Rotation == "" || len(p.Lanes) != 1 || p.Lanes[0].Blocked != 0 || p.Lanes[0].WindOutput() != 1 {
			t.Errorf("%s: rotation %q lanes %+v", p.Node, p.Rotation, p.Lanes)
		}
		if p.Footprint.Area() != 14 {
			t.Errorf("%s: footprint %d cells", p.Node, p.Footprint.Area())
		}
	}
	for _, d := range got.Decisions {
		if d.Verdict != EmbedPlace {
			t.Errorf("decision %+v", d)
		}
	}
	again, _ := EmbedSites(in)
	if !slices.EqualFunc(got.Sites, again.Sites, func(a, b SitePlacement) bool {
		return a.Node == b.Node && a.Origin == b.Origin && a.Rotation == b.Rotation
	}) {
		t.Error("not deterministic")
	}
}

// A footprint standing in a lane costs that lane output, and the embedder
// reports it without moving anything.
func TestEmbedSitesReportsAWindLaneBlockedByAFootprint(t *testing.T) {
	anchor := domain.Cell{X: 40, Z: 40}
	blocker := rectNode("battery", 2, 2)
	blocker.Anchor = &anchor
	turbine := turbineNode("wind-1")
	turbine.Alts = nil // one turn, so the lane is known
	at := domain.Cell{X: 38, Z: 30}
	turbine.Anchor = &at
	got, err := EmbedSites(siteInput(t, blocker, turbine))
	if err != nil {
		t.Fatal(err)
	}
	p, ok := got.Site("wind-1")
	if !ok || !p.Lanes[0].Region.Overlaps(RectRegion(Rectangle{X: 40, Z: 40, Width: 2, Height: 2})) {
		t.Fatalf("lane does not reach the footprint: %+v", p)
	}
	if p.Lanes[0].Blocked != 4 || p.Lanes[0].WindOutput() < 0.19 || p.Lanes[0].WindOutput() > 0.21 {
		t.Errorf("blocked %d, output %v", p.Lanes[0].Blocked, p.Lanes[0].WindOutput())
	}
	if !slices.ContainsFunc(got.Decisions, func(d SiteDecision) bool { return d.Node == "wind-1" && d.Reason == ReasonSiteWindBlocked }) {
		t.Errorf("decisions %+v", got.Decisions)
	}
}

func TestPowerSiteNodesFollowThePlan(t *testing.T) {
	p := PlanUtilities(batteryRoomPlan(), UtilityWants{Geysers: []Rectangle{{X: 20, Z: 190, Width: 2, Height: 2}}})
	for i := range int32(2) {
		p.Sites = append(p.Sites,
			PlannedSite{Kind: NodeTurbine, SitePlacement: SitePlacement{Node: windNodeID(int(i) + 1), Footprint: RectRegion(Rectangle{X: 60, Z: 10 + 4*i, Width: 7, Height: 2}), Rotation: domain.North}},
			PlannedSite{Kind: NodeSolar, SitePlacement: SitePlacement{Node: solarNodeID(int(i) + 1), Footprint: RectRegion(Rectangle{X: 80, Z: 10 + 5*i, Width: 4, Height: 4})}})
	}
	nodes := PowerSiteNodes(p)
	var ids []string
	for _, n := range nodes {
		ids = append(ids, n.ID)
	}
	if want := []string{"wind-1", "wind-2", "solar-1", "solar-2", "geothermal-1"}; !slices.Equal(ids, want) {
		t.Fatalf("nodes %v, want %v", ids, want)
	}
	geo := nodes[4]
	if geo.Anchor == nil || geo.Footprint.Area() != int(geothermalSide+2*geothermalShell)*int(geothermalSide+2*geothermalShell) {
		t.Errorf("geothermal %+v", geo)
	}
}
