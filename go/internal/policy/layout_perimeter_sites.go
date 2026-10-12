package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The defense ground as site nodes (#2922). PlanPerimeter stores what it
// traces as PlannedSites on the plan, one node each for the ring, every gate,
// the killbox opening, the approach legs, the fence, each moisture pump, the
// mortar and the cover-clear band, instead of as reservations; the defense
// layout, the wall sections and the room placers read the nodes. A node is the
// SitePlacement of #2884 (footprint in map cells, an anchor flag, the room it
// replaces) plus its kind, the node it replaces, and the reason it stands.
//
// Bridges, light footing and open gaps are attributes of the ring, not nodes:
// each is a subset of the wall (or, for a gap, the ring ground nothing can
// close) that the wall sections read cell by cell, and none has a lifecycle or
// a placement of its own.

// NodeKind is what a planned site is. The strings are the footprints' names in
// overlays and ledgers.
type NodeKind string

const (
	NodeRing       NodeKind = "perimeter_wall"
	NodeGate       NodeKind = "gate"
	NodeKillbox    NodeKind = "killbox"
	NodeApproach   NodeKind = "killbox_approach"
	NodeFence      NodeKind = "killbox_fence"
	NodePump       NodeKind = "perimeter_pump"
	NodeMortar     NodeKind = "mortar"
	NodeCoverClear NodeKind = "cover_clear"
	// The dark pockets beside the base (#2925) are produced after the ring
	// node on the same model and walled solid. They stand and fall with the
	// ring, and their sections keep the perimeter tier's names.
	NodePocketWall NodeKind = "pocket_wall"
)

// Why the ring and its opening stand as they do; the caller journals them as
// layout_plan rows (PerimeterDecisions).
const (
	ReasonRingTraced = "ring_traced"
	// ReasonOpeningRoutes: the dry ring position most raider routes cross. A
	// replan keeps the opening while it still opens (the defense layout is
	// anchored on it), and with it this reason.
	ReasonOpeningRoutes = "opening_most_routes"
	// ReasonOpeningNone: no route reaches a dry ring position that opens.
	ReasonOpeningNone = "opening_none"
)

// RingFacts are the ring's own attributes.
type RingFacts struct {
	// Light is the wall cells on light footing (the wall there is wood) and
	// Bridged those on water (a bridge goes down first). Both lie within the
	// ring's footprint.
	Light, Bridged Region `json:",omitzero"`
	// Gaps are the walkable ring cells nothing can close.
	Gaps Region `json:",omitzero"`
	// Inner is the ground strictly inside the ring (the base region) and
	// Sealed that ground with the ring itself, the opening and the terrain
	// stretches included. Consumers read the colony's enclosed ground here
	// (herd areas, pens, pockets, the prison, the pumps' conduit).
	Inner, Sealed Region `json:",omitzero"`
	// Opening is why the killbox opening is where it is (the Reason*Opening
	// values), including why there is none.
	Opening string
}

// PlannedSite is one stored site node.
type PlannedSite struct {
	SitePlacement
	Kind NodeKind
	// ReplacesSite is the ID of the site node this one supersedes; empty for
	// none. The old and the new ring coexist as two nodes until the old one
	// is dropped.
	ReplacesSite string `json:",omitempty"`
	Reason       string `json:",omitempty"`
	// Ring is set on the ring node only.
	Ring *RingFacts `json:",omitempty"`
}

// perimeterSiteKinds are the nodes that stand or fall with the ring:
// PlanPerimeter owns them (and drops the lot when it traces again), the pocket
// walls are produced after it.
var perimeterSiteKinds = map[NodeKind]bool{NodeRing: true, NodeGate: true, NodeKillbox: true, NodeApproach: true, NodeFence: true, NodePump: true, NodeMortar: true, NodeCoverClear: true, NodePocketWall: true}

func newSite(id string, kind NodeKind, fp Region, reason string) PlannedSite {
	b := fp.Bounds()
	return PlannedSite{SitePlacement: SitePlacement{Node: id, Origin: domain.Cell{X: b.X, Z: b.Z}, Footprint: fp}, Kind: kind, Reason: reason}
}

// LiveSites are the plan's nodes of kind that no other node replaces, in plan
// order.
func (p LayoutPlan) LiveSites(kind NodeKind) []PlannedSite {
	var out []PlannedSite
	for _, s := range p.Sites {
		if s.Kind != kind || p.replacedSite(s.Node) {
			continue
		}
		out = append(out, s)
	}
	return out
}

func (p LayoutPlan) replacedSite(id string) bool {
	return slices.ContainsFunc(p.Sites, func(o PlannedSite) bool { return o.ReplacesSite == id })
}

// Ring is the live ring node.
func (p LayoutPlan) Ring() (PlannedSite, bool) {
	if rings := p.LiveSites(NodeRing); len(rings) > 0 && rings[0].Ring != nil {
		return rings[0], true
	}
	return PlannedSite{}, false
}

// Killbox is the live killbox opening node.
func (p LayoutPlan) Killbox() (PlannedSite, bool) {
	if k := p.LiveSites(NodeKillbox); len(k) > 0 {
		return k[0], true
	}
	return PlannedSite{}, false
}

// Reserved is the ground the plan holds: its reservations and the footprint of
// every site node, for the placers that keep clear of whatever is claimed.
func (p LayoutPlan) Reserved() []LayoutReservation {
	if len(p.Sites) == 0 {
		return p.Reservations
	}
	out := slices.Clone(p.Reservations)
	for _, s := range p.Sites {
		out = append(out, LayoutReservation{Kind: ReservationKind(s.Kind), Area: s.Footprint})
		if s.Ring != nil && !s.Ring.Gaps.Empty() {
			out = append(out, LayoutReservation{Kind: ReservationKind(s.Kind), Area: s.Ring.Gaps})
		}
	}
	return out
}

// WallCells are the cells the ring holds: its wall, the gaps nothing can
// close and the gates.
func (p LayoutPlan) WallCells() map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, s := range p.LiveSites(NodeRing) {
		for _, c := range s.Footprint.Cells() {
			out[c] = true
		}
		if s.Ring != nil {
			for _, c := range s.Ring.Gaps.Cells() {
				out[c] = true
			}
		}
	}
	for _, s := range p.LiveSites(NodeGate) {
		for _, c := range s.Footprint.Cells() {
			out[c] = true
		}
	}
	return out
}

// withoutPerimeterSites drops the nodes that stand or fall with the ring.
func (p LayoutPlan) withoutPerimeterSites() LayoutPlan {
	var kept []PlannedSite
	for _, s := range p.Sites {
		if !perimeterSiteKinds[s.Kind] {
			kept = append(kept, s)
		}
	}
	p.Sites = kept
	return p
}

// withoutSites drops the nodes of the given kinds.
func (p LayoutPlan) withoutSites(kinds ...NodeKind) LayoutPlan {
	var kept []PlannedSite
	for _, s := range p.Sites {
		if !slices.Contains(kinds, s.Kind) {
			kept = append(kept, s)
		}
	}
	p.Sites = kept
	return p
}

// PerimeterDecisions are the ring's and the opening's decisions, for the
// caller to journal: the ring is traced, and the opening is kept, chosen by
// the routes, or absent; then then each pocket wall placed.
func PerimeterDecisions(p LayoutPlan) []SiteDecision {
	ring, ok := p.Ring()
	if !ok {
		return nil
	}
	out := []SiteDecision{{Node: ring.Node, Verdict: EmbedPlace, Reason: ring.Reason}}
	if k, ok := p.Killbox(); ok {
		out = append(out, SiteDecision{Node: k.Node, Verdict: EmbedPlace, Reason: k.Reason})
	} else {
		out = append(out, SiteDecision{Node: string(NodeKillbox), Verdict: EmbedUnplaceable, Reason: ring.Ring.Opening})
	}
	for _, s := range p.LiveSites(NodePocketWall) {
		out = append(out, SiteDecision{Node: s.Node, Verdict: EmbedPlace, Reason: s.Reason})
	}
	return out
}

// PerimeterChanged reports whether next traces other defense ground than prev.
func PerimeterChanged(prev, next LayoutPlan) bool { return !sameSites(prev, next) }

// sameSites reports whether a and b hold the same perimeter nodes: the same
// ground, whatever the reasons they stand (a kept opening reads as chosen).
func sameSites(a, b LayoutPlan) bool {
	pick := func(p LayoutPlan) []PlannedSite {
		var out []PlannedSite
		for _, s := range p.Sites {
			if perimeterSiteKinds[s.Kind] {
				out = append(out, s)
			}
		}
		return out
	}
	x, y := pick(a), pick(b)
	return slices.EqualFunc(x, y, func(m, n PlannedSite) bool {
		return m.Node == n.Node && m.Kind == n.Kind && m.Footprint.Equal(n.Footprint) && m.Anchored == n.Anchored && m.ReplacesSite == n.ReplacesSite && sameRing(m.Ring, n.Ring)
	})
}

func sameRing(a, b *RingFacts) bool {
	if a == nil || b == nil {
		return a == b
	}
	return a.Light.Equal(b.Light) && a.Bridged.Equal(b.Bridged) && a.Gaps.Equal(b.Gaps) && a.Inner.Equal(b.Inner) && a.Sealed.Equal(b.Sealed)
}

// regionRects is the region as the rectangles the wall sections cut: the same
// decomposition cellRects makes of its cells.
func regionRects(g Region) []Rectangle {
	set := map[domain.Cell]bool{}
	for _, c := range g.Cells() {
		set[c] = true
	}
	return cellRects(set)
}
