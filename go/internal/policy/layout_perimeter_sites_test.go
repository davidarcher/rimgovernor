package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// testSite is a node of kind standing on the union of rects.
func testSite(kind NodeKind, rects ...Rectangle) PlannedSite {
	var runs []RowRun
	for _, r := range rects {
		runs = append(runs, RectRegion(r).Runs...)
	}
	s := newSite(string(kind), kind, RegionOfRuns(runs), "")
	if kind == NodeRing {
		s.Ring = &RingFacts{}
	}
	return s
}

// withInterior stores the ring's inside, flooded from the plan's rooms with no
// cover-clear band to stop it.
func withInterior(p LayoutPlan) LayoutPlan {
	sites := make([]PlannedSite, len(p.Sites))
	for i, s := range p.Sites {
		if s.Ring != nil {
			facts := *s.Ring
			facts.Inner, facts.Sealed = traceInterior(p.AllRooms(), p.WallCells(), nil)
			s.Ring = &facts
		}
		sites[i] = s
	}
	p.Sites = sites
	return p
}

// The old and the new ring coexist as two nodes: the one that replaces the
// other is the live ring, and the replaced wall drops out of the plan's ground
// until it is dropped.
func TestRingNodeReplacesTheOldRing(t *testing.T) {
	old := testSite(NodeRing, Rectangle{X: 10, Z: 10, Width: 20, Height: 3})
	old.Node = "ring-old"
	next := testSite(NodeRing, Rectangle{X: 5, Z: 5, Width: 30, Height: 3})
	next.Node, next.ReplacesSite = "ring-new", "ring-old"
	p := LayoutPlan{Sites: []PlannedSite{old, next}}
	ring, ok := p.Ring()
	if !ok || ring.Node != "ring-new" || len(p.LiveSites(NodeRing)) != 1 {
		t.Fatalf("live ring %+v", ring)
	}
	if p.WallCells()[domain.Cell{X: 12, Z: 11}] {
		t.Fatal("the replaced ring still holds the wall")
	}
	p.Sites = p.Sites[:1]
	if ring, _ = p.Ring(); ring.Node != "ring-old" {
		t.Fatal("without its successor the old ring is live", ring.Node)
	}
}

// The ring, the opening and the base region are stored on nodes, the same
// inputs store the same nodes, and the decisions name why the ring and the
// opening stand: chosen by the routes, and kept as it was by a replan.
func TestPerimeterStoresNodesWithDecisions(t *testing.T) {
	flat := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	p := perimeterPlan(t, flat)
	s := zoningSurvey(200, flat)
	if again := PlanPerimeter(p.withoutPerimeterSites(), s); !reflect.DeepEqual(again.Sites, p.Sites) {
		t.Fatal("the same inputs stored different nodes")
	}
	ring, ok := p.Ring()
	if !ok || ring.Ring.Inner.Empty() || ring.Ring.Sealed.Area() < ring.Ring.Inner.Area() {
		t.Fatal("ring node without a stored base region", ring.Ring)
	}
	for _, r := range p.Rooms {
		if !ring.Ring.Inner.Contains(domain.Cell{X: r.Region.Bounds().X, Z: r.Region.Bounds().Z}) {
			t.Fatal("room outside the stored base region", r.Role)
		}
	}
	if k, ok := p.Killbox(); !ok || !k.Anchored {
		t.Fatal("the opening is anchored", k)
	}
	want := []SiteDecision{{Node: "perimeter-ring", Verdict: EmbedPlace, Reason: ReasonRingTraced}, {Node: "killbox", Verdict: EmbedPlace, Reason: ReasonOpeningRoutes}}
	if got := PerimeterDecisions(p); !reflect.DeepEqual(got, want) {
		t.Fatalf("decisions %+v, want %+v", got, want)
	}
	// A replan keeps the opening, its anchor and its reason: nothing changes.
	if replanned := PlanPerimeter(p, s); !reflect.DeepEqual(replanned.Sites, p.Sites) {
		t.Fatal("a kept opening changed the stored nodes")
	}
}
