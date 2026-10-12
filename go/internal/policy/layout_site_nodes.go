package policy

import (
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Site nodes (#2884): the things the colony builds that have no walls and no
// door (turbines, solar, geothermal, the ring, gates, the killbox) as nodes of
// the station graph, beside the room nodes. One type serves them all. A node
// has a footprint of any shape, in cells relative to its origin; an optional
// fixed Anchor (a geyser, a vent) that the origin lands on; and Lanes, regions
// that travel with the footprint but reserve nothing: a turbine's wind lane
// may lie across fields and across other turbines' lanes. The embedder never
// refuses or moves a node for a lane, and prices none: what the game charges
// for a blocked lane cell (the lost wind output) is the consumers' to price in
// game units, once they move onto the node. Footprints are exclusive: they
// stay off the rooms' walls and off each other, on ground the obstacle level
// the rooms were placed under leaves free. Like a room it may Replace a
// planned room. Nothing in the graph builder emits site nodes yet and the old
// planner stays authoritative.

// ErrSiteNode marks a site node the embedder cannot read.
var ErrSiteNode = errors.New("site node")

// Embedding decisions for site nodes; the caller journals them.
const (
	ReasonSiteAnchored = "site_placed_at_anchor"
	ReasonSiteFree     = "site_placed_in_envelope"
	ReasonSiteNoGround = "site_no_ground"
	// ReasonSiteAnchorBlocked: the anchor is fixed, so the node stands there
	// although its footprint touches a room's walls or another footprint.
	ReasonSiteAnchorBlocked = "site_anchor_blocked"
)

// SiteLane is a region that belongs to a site node and is never an exclusive
// reservation. Cells are relative to the node's origin.
type SiteLane struct {
	ID     string
	Region Region
	Reason string
}

// SiteNode is a site element of the station graph.
type SiteNode struct {
	ID string
	// Footprint is the cells the node stands on, relative to its origin.
	Footprint Region
	// Anchor is the cell the origin must land on; nil for a node that goes
	// anywhere the envelope and its band allow.
	Anchor *domain.Cell
	Lanes  []SiteLane
	// Band is the ground the footprint keeps from the rooms: at least Min clear
	// cells from every room's walls and, while any room stands, no more than Max
	// from the nearest. The zero band is no constraint.
	Band DistanceBand
	// Replaces is the planned room ID this node supersedes; 0 for none.
	Replaces int32
	Reason   string
}

// SitePlacement is where a site node landed. Lanes are in map cells, as the
// footprint is.
type SitePlacement struct {
	Node      string
	Origin    domain.Cell
	Footprint Region
	Lanes     []SiteLane
	Anchored  bool
	Replaces  int32
}

// SiteDecision is one site decision for the caller to journal.
type SiteDecision struct {
	Node    string
	Verdict string
	Reason  string
}

// SiteEmbedInput is the site nodes to place around an embedding of the rooms.
// Embed is the input the embedding was made from; Embedding is its result.
type SiteEmbedInput struct {
	Embed     EmbedInput
	Embedding Embedding
	Nodes     []SiteNode
}

// SiteEmbedding is the placement of the site nodes.
type SiteEmbedding struct {
	Sites     []SitePlacement
	Decisions []SiteDecision
}

// Site is the placement of the node.
func (e SiteEmbedding) Site(id string) (SitePlacement, bool) {
	for _, s := range e.Sites {
		if s.Node == id {
			return s, true
		}
	}
	return SitePlacement{}, false
}

// siteClear is the clear ground in cells between a footprint (no walls) and a
// room's interior: the room's wall ring is the first cell beyond it.
func siteClear(site, room Rectangle) int {
	return max(axisGap(site.X, site.Width, room.X, room.Width), axisGap(site.Z, site.Height, room.Z, room.Height)) - 1
}

// EmbedSites places the site nodes around the rooms. Anchored nodes stand
// first, on their anchors; the rest take, in turn, the origin of least site
// cost (the embedding's soil, edge and centre terms over the footprint's
// cells), ties to the lowest cell, so the result depends only on the inputs.
func EmbedSites(in SiteEmbedInput) (SiteEmbedding, error) {
	ids := map[string]bool{}
	for _, n := range in.Nodes {
		if n.ID == "" || ids[n.ID] || n.Footprint.Empty() {
			return SiteEmbedding{}, fmt.Errorf("%w: %q needs a unique id and a footprint", ErrSiteNode, n.ID)
		}
		ids[n.ID] = true
	}
	depth, _, err := roomLimits(in.Embed.RoofDistance)
	if err != nil {
		return SiteEmbedding{}, err
	}
	scorer := newPlanScorer(in.Embed.Zones, in.Embed.Reservations, in.Embed.Survey)
	e := newEmbedder(in.Embed, depth, scorer, in.Embedding.Level)
	var rooms []Rectangle
	for _, b := range in.Embedding.Blocks {
		rooms = append(rooms, b.Interior)
	}
	order := slices.Clone(in.Nodes)
	slices.SortStableFunc(order, func(a, b SiteNode) int { return b2i(b.Anchor != nil) - b2i(a.Anchor != nil) })
	var out SiteEmbedding
	var taken []Region
	for _, n := range order {
		p := SitePlacement{Node: n.ID, Replaces: n.Replaces}
		verdict, reason := EmbedPlace, ReasonSiteAnchored
		if n.Anchor != nil {
			p.Origin, p.Anchored = *n.Anchor, true
			p.Footprint = n.Footprint.Translate(p.Origin.X, p.Origin.Z)
			if !siteFree(p.Footprint, rooms, taken) {
				verdict, reason = EmbedUnsatisfied, ReasonSiteAnchorBlocked
			}
		} else {
			origin, ok := e.siteOrigin(n, rooms, taken)
			if !ok {
				out.Decisions = append(out.Decisions, SiteDecision{Node: n.ID, Verdict: EmbedUnplaceable, Reason: ReasonSiteNoGround})
				continue
			}
			p.Origin, p.Footprint = origin, n.Footprint.Translate(origin.X, origin.Z)
			reason = ReasonSiteFree
		}
		for _, l := range n.Lanes {
			p.Lanes = append(p.Lanes, SiteLane{ID: l.ID, Region: l.Region.Translate(p.Origin.X, p.Origin.Z), Reason: l.Reason})
		}
		taken = append(taken, p.Footprint)
		out.Sites = append(out.Sites, p)
		out.Decisions = append(out.Decisions, SiteDecision{Node: n.ID, Verdict: verdict, Reason: reason})
	}
	return out, nil
}

// siteFree reports a footprint off the rooms' walls and off the footprints
// already taken. Lanes never enter into it.
func siteFree(fp Region, rooms []Rectangle, taken []Region) bool {
	bounds := fp.Bounds()
	for _, r := range rooms {
		if collides(bounds, r) {
			return false
		}
	}
	return !slices.ContainsFunc(taken, fp.Overlaps)
}

// siteOrigin is the origin of least cost where the node's footprint fits the
// envelope, its band and the taken ground.
func (e *embedder) siteOrigin(n SiteNode, rooms []Rectangle, taken []Region) (domain.Cell, bool) {
	var best domain.Cell
	bestCost, found := 0, false
	shape := n.Footprint.Bounds()
	for _, o := range e.core {
		if !e.level.inside(Rectangle{X: shape.X + o.X, Z: shape.Z + o.Z, Width: shape.Width, Height: shape.Height}) {
			continue
		}
		fp := n.Footprint.Translate(o.X, o.Z)
		cost, ok := e.siteCost(fp)
		if !ok || !siteFree(fp, rooms, taken) || !siteBand(n.Band, fp.Bounds(), rooms) {
			continue
		}
		if !found || cost < bestCost {
			best, bestCost, found = o, cost, true
		}
	}
	return best, found
}

// siteCost is the cost of a footprint on the site, in the embedding's terms;
// false when a cell lies off the level's core.
func (e *embedder) siteCost(fp Region) (int, bool) {
	cost := 0
	for _, c := range fp.Cells() {
		cell := Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}
		if !e.level.inside(cell) || e.level.sum(cell) != 1 {
			return 0, false
		}
		cost -= int(e.soil.sum(cell) + e.centre.sum(cell) + e.edge.sum(cell))
	}
	return cost, true
}

func siteBand(band DistanceBand, fp Rectangle, rooms []Rectangle) bool {
	if !band.Set() || len(rooms) == 0 {
		return true
	}
	nearest := siteClear(fp, rooms[0])
	for _, r := range rooms {
		nearest = min(nearest, siteClear(fp, r))
	}
	return nearest >= int(band.Min) && (band.Max <= 0 || nearest <= int(band.Max))
}
