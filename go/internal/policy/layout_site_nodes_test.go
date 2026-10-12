package policy

import (
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func siteInput(t *testing.T, nodes ...SiteNode) SiteEmbedInput {
	t.Helper()
	in := embedInput(t, plainScoreSurvey(), 3)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	return SiteEmbedInput{Embed: in, Embedding: emb, Nodes: nodes}
}

func rectNode(id string, w, h int32) SiteNode {
	return SiteNode{ID: id, Footprint: RectRegion(Rectangle{Width: w, Height: h})}
}

// A fixed-anchor node lands on its anchor, wherever that is.
func TestSiteNodeLandsOnItsAnchor(t *testing.T) {
	anchor := domain.Cell{X: 5, Z: 7}
	n := rectNode("geyser", 2, 2)
	n.Anchor = &anchor
	n.Replaces = 4
	got, err := EmbedSites(siteInput(t, n))
	if err != nil {
		t.Fatal(err)
	}
	p, ok := got.Site("geyser")
	if !ok || !p.Anchored || p.Origin != anchor || p.Replaces != 4 || !p.Footprint.Contains(anchor) || p.Footprint.Area() != 4 {
		t.Fatalf("placement %+v", p)
	}
	if d := got.Decisions[0]; d.Verdict != EmbedPlace || d.Reason != ReasonSiteAnchored {
		t.Errorf("decision %+v", d)
	}
}

// A free node lands inside the envelope and off the rooms.
func TestSiteNodeFreeStaysInEnvelope(t *testing.T) {
	n := rectNode("solar", 3, 3)
	in := siteInput(t, n)
	in.Price = SitePrice{Weights: DefaultSiteTypeWeights(), ConduitW: 6.5, WallW: 32}
	got, err := EmbedSites(in)
	if err != nil {
		t.Fatal(err)
	}
	p, ok := got.Site("solar")
	if !ok || p.Anchored {
		t.Fatalf("no free placement: %+v", got)
	}
	for _, b := range in.Embedding.Blocks {
		if p.Footprint.Overlaps(RectRegion(pad(b.Interior, 1))) {
			t.Errorf("footprint %+v touches the walls of %+v", p.Footprint.Bounds(), b.Interior)
		}
	}
	if d := got.Decisions[0]; d.Reason != ReasonSiteFree {
		t.Errorf("decision %+v", d)
	}
}

// Lanes overlap a field and other lanes without refusal; footprints stay apart.
func TestSiteLanesOverlapFieldsAndEachOther(t *testing.T) {
	lane := SiteLane{ID: "wind", Region: RectRegion(Rectangle{X: -10, Z: 1, Width: 21, Height: 6}), Reason: "wind_lane"}
	a, b := rectNode("turbine-a", 2, 2), rectNode("turbine-b", 2, 2)
	a.Lanes, b.Lanes = []SiteLane{lane}, []SiteLane{lane}
	in := siteInput(t, a, b)
	if len(in.Embed.Zones) == 0 {
		t.Fatal("survey has no zones")
	}
	got, err := EmbedSites(in)
	if err != nil {
		t.Fatal(err)
	}
	pa, oka := got.Site("turbine-a")
	pb, okb := got.Site("turbine-b")
	if !oka || !okb || len(got.Decisions) != 2 {
		t.Fatalf("placements %+v", got)
	}
	if pa.Footprint.Overlaps(pb.Footprint) {
		t.Error("footprints overlap")
	}
	if !pa.Lanes[0].Region.Overlaps(pb.Lanes[0].Region) {
		t.Errorf("lanes %+v and %+v do not overlap", pa.Lanes[0].Region.Bounds(), pb.Lanes[0].Region.Bounds())
	}
	field := false
	for _, z := range in.Embed.Zones {
		field = field || z.Kind == ZoneField && RegionOfRuns(z.Runs).Overlaps(pa.Lanes[0].Region)
	}
	if !field {
		t.Error("lane lies over no field cell")
	}
	for _, d := range got.Decisions {
		if d.Verdict != EmbedPlace {
			t.Errorf("decision %+v", d)
		}
	}
}

func TestSiteEmbeddingDeterministicAndRefusesBadNodes(t *testing.T) {
	anchor := domain.Cell{X: 30, Z: 30}
	geyser := rectNode("geyser", 2, 2)
	geyser.Anchor = &anchor
	in := siteInput(t, rectNode("a", 2, 2), geyser, rectNode("b", 3, 1))
	one, err := EmbedSites(in)
	if err != nil {
		t.Fatal(err)
	}
	two, _ := EmbedSites(in)
	if !reflect.DeepEqual(one, two) || len(one.Sites) != 3 {
		t.Fatalf("not deterministic: %+v vs %+v", one, two)
	}
	for _, bad := range []SiteNode{{ID: "empty"}, {ID: "", Footprint: RectRegion(Rectangle{Width: 1, Height: 1})}} {
		if _, err := EmbedSites(siteInput(t, bad)); !errors.Is(err, ErrSiteNode) {
			t.Errorf("node %+v: err %v", bad, err)
		}
	}
	if _, err := EmbedSites(siteInput(t, rectNode("d", 1, 1), rectNode("d", 1, 1))); !errors.Is(err, ErrSiteNode) {
		t.Errorf("duplicate id: %v", err)
	}
	if got, _ := EmbedSites(siteInput(t, rectNode("huge", 500, 500))); len(got.Sites) != 0 || got.Decisions[0].Reason != ReasonSiteNoGround {
		t.Errorf("huge node: %+v", got)
	}
}

// The graph builder emits no site nodes, so the advisory chain is unchanged.
func TestGraphBuilderEmitsNoSiteNodes(t *testing.T) {
	g, err := BuildStationGraph(graphTestInput())
	if err != nil {
		t.Fatal(err)
	}
	if len(g.Sites) != 0 {
		t.Fatalf("sites %+v", g.Sites)
	}
}
