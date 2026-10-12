package policy

import (
	"fmt"
	"regexp"
	"slices"
)

// Replacing the ring (#2924, epic #2855). A ring traced again while the
// defense record still holds the old one is not edited in place: the planner
// stores the new ring, its gates, fence and pumps under revision-suffixed ids
// beside the old ones, which stay in the plan as retired nodes (replaced by the
// new ring: off the live ground, still on the reserved ground). The runtime
// builds the new generation while the old one defends, opens the old wall only
// once the new ring stands, and demolishes the old through removal tiers that
// name the retired nodes; the retired nodes leave the plan once the record no
// longer names them. One rebuild runs at a time: while the record holds a
// retired node or a removal tier the ring is not traced again.

// PerimeterHold is what the defense record holds of the perimeter.
type PerimeterHold struct {
	// Held are the ids of the site nodes a defense tier names: built, being
	// built, or waiting to be demolished.
	Held map[string]bool
	// Removing is set while a removal tier of the perimeter stands in the
	// record: a rebuild is in flight.
	Removing bool
}

// perimeterFamily are the node kinds whose ids carry the ring's revision: the
// ring and what the defense record cuts from it.
var perimeterFamily = map[NodeKind]bool{NodeRing: true, NodeGate: true, NodeFence: true, NodePump: true}

var revisionSuffix = regexp.MustCompile(`-r\d+$`)

// revisedID is id at revision rev: the base id, suffixed from the first
// replacement on.
func revisedID(id string, rev int) string {
	id = revisionSuffix.ReplaceAllString(id, "")
	if rev == 0 {
		return id
	}
	return fmt.Sprintf("%s-r%d", id, rev)
}

// ringRevision is the live ring's revision (0 without one).
func (p LayoutPlan) ringRevision() int {
	if ring, ok := p.Ring(); ok {
		return ring.Ring.Revision
	}
	return 0
}

// withRevision names the ring and its family at revision rev.
func (p LayoutPlan) withRevision(rev int) LayoutPlan {
	sites := make([]PlannedSite, len(p.Sites))
	for i, s := range p.Sites {
		if perimeterFamily[s.Kind] {
			s.Node = revisedID(s.Node, rev)
			if s.Ring != nil {
				facts := *s.Ring
				facts.Revision = rev
				s.Ring = &facts
			}
		}
		sites[i] = s
	}
	p.Sites = sites
	return p
}

// retiredHeld reports a retired node the record still names.
func (h PerimeterHold) retiredHeld(p LayoutPlan) bool {
	return slices.ContainsFunc(p.Sites, func(s PlannedSite) bool { return p.replacedSite(s.Node) && h.Held[s.Node] })
}

// rebuilding reports a rebuild in flight: a retired node is still held, or the
// record has a removal tier standing.
func (h PerimeterHold) rebuilding(p LayoutPlan) bool { return h.Removing || h.retiredHeld(p) }

// PerimeterRetirable reports retired nodes in the plan that the record no
// longer names: a replan drops them.
func PerimeterRetirable(p LayoutPlan, held map[string]bool) bool {
	return slices.ContainsFunc(p.Sites, func(s PlannedSite) bool { return p.replacedSite(s.Node) && !held[s.Node] })
}

// withoutUnheldRetired drops the retired nodes the record does not name.
func (p LayoutPlan) withoutUnheldRetired(held map[string]bool) LayoutPlan {
	gone := map[string]bool{}
	for _, s := range p.Sites {
		if p.replacedSite(s.Node) && !held[s.Node] {
			gone[s.Node] = true
		}
	}
	if len(gone) == 0 {
		return p
	}
	sites := make([]PlannedSite, 0, len(p.Sites))
	for _, s := range p.Sites {
		if gone[s.Node] {
			continue
		}
		s.ReplacesSites = slices.DeleteFunc(slices.Clone(s.ReplacesSites), func(id string) bool { return gone[id] })
		if len(s.ReplacesSites) == 0 {
			s.ReplacesSites = nil
		}
		sites = append(sites, s)
	}
	p.Sites = sites
	return p
}

// withPerimeterSitesOf is p with the perimeter's nodes as from holds them,
// retired ones included.
func (p LayoutPlan) withPerimeterSitesOf(from LayoutPlan) LayoutPlan {
	p = p.withoutPerimeterSites()
	for _, s := range from.Sites {
		if perimeterSiteKinds[s.Kind] {
			p.Sites = append(p.Sites, s)
		}
	}
	return p
}

// SiteView is the plan as the named nodes alone make it: they are live
// whatever replaced them, and the live killbox stays for the sections to order
// by. The wall sections of a retired generation are cut from it.
func (p LayoutPlan) SiteView(ids []string) LayoutPlan {
	var sites []PlannedSite
	for _, s := range p.Sites {
		switch {
		case slices.Contains(ids, s.Node):
			s.ReplacesSites = nil
			sites = append(sites, s)
		case s.Kind == NodeKillbox && !p.replacedSite(s.Node):
			sites = append(sites, s)
		}
	}
	p.Sites = sites
	return p
}

// sameFamily reports whether a and b hold the same ring, gates, fence and
// pumps: a ring traced again over the same ground replaces nothing.
// The ground inside the ring is no part of it: rooms added inside change that.
func sameFamily(a, b LayoutPlan) bool {
	pick := func(p LayoutPlan) []PlannedSite {
		var out []PlannedSite
		for _, s := range p.Sites {
			if perimeterFamily[s.Kind] && !p.replacedSite(s.Node) {
				if s.Ring != nil {
					facts := RingFacts{Light: s.Ring.Light, Bridged: s.Ring.Bridged, Gaps: s.Ring.Gaps, Revision: s.Ring.Revision}
					s.Ring = &facts
				}
				out = append(out, s)
			}
		}
		return out
	}
	return slices.EqualFunc(pick(a), pick(b), func(m, n PlannedSite) bool {
		return m.Node == n.Node && m.Kind == n.Kind && m.Footprint.Equal(n.Footprint) && sameRing(m.Ring, n.Ring)
	})
}

// LayPerimeter (the perimeter step of ReplanLayout) lays the perimeter around next, the plan a replan grew from
// prev. While the record holds a retired node or a removal tier the ring is
// not traced again (one rebuild at a time). A ring that comes out other than
// prev's, with prev's held by the record, is laid as a replacement beside the
// old one.
func LayPerimeter(prev, next LayoutPlan, s MapSurvey, hold PerimeterHold) LayoutPlan {
	prev, next = prev.withoutUnheldRetired(hold.Held), next.withoutUnheldRetired(hold.Held)
	if hold.rebuilding(prev) {
		return next.withPerimeterSitesOf(prev)
	}
	laid := withoutCore(PlanMountainPockets(PlanPerimeter(next, s), s))
	return laid.replacing(prev, hold)
}

// replacing turns a ring traced over the held one into its replacement.
func (p LayoutPlan) replacing(prev LayoutPlan, hold PerimeterHold) LayoutPlan {
	old, had := prev.Ring()
	if !had || !hold.Held[old.Node] || sameFamily(prev, p) {
		return p
	}
	p = p.withRevision(old.Ring.Revision + 1)
	var retired []PlannedSite
	var ids []string
	for _, s := range prev.Sites {
		if perimeterFamily[s.Kind] && !prev.replacedSite(s.Node) && hold.Held[s.Node] {
			retired, ids = append(retired, s), append(ids, s.Node)
		}
	}
	for i, s := range p.Sites {
		if s.Kind == NodeRing && s.Ring != nil {
			s.ReplacesSites, s.Reason = ids, ReasonRingReplaced
			p.Sites[i] = s
			break
		}
	}
	p.Sites = append(p.Sites, retired...)
	return p
}
