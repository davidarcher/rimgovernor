package policy

import (
	"slices"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Conduits (#2889): the planner owns the power conduit paths. A tiled plan
// carries one ConduitLink per routed end, derived with the plan. Conduits run
// under walls, doors and furniture (the Conduits layer; ConduitCells), so they
// compete for no floor: the coupling is path length and, later, the lifecycle
// of the room a path crosses. power_method.go still decides which sites
// connect, by which method and at what budget; a planned link is what the
// planner would lay, not an order.
//
// Topology: one net grows. The first power site is the root; every later site,
// then every consuming room, routes from its own cells to the nearest cell of
// the net so far (the root's cells and every laid path), so cells are shared
// and no star forms on any node, the battery room included.

// ConduitRef names one end of a link: "room:<id>" for a planned room or
// "site:<node id>" for a site: a stored turbine or solar plot by its node id,
// a geothermal enclosure or battery-room reservation by ReservationSiteID.
type ConduitRef string

const (
	conduitRoomTag = "room:"
	conduitSiteTag = "site:"
)

// RoomRef is the ref of planned room id.
func RoomRef(id int32) ConduitRef { return ConduitRef(conduitRoomTag + strconv.Itoa(int(id))) }

// SiteRef is the ref of the site node with the given id.
func SiteRef(node string) ConduitRef { return ConduitRef(conduitSiteTag + node) }

// Room is the room id of a room ref.
func (r ConduitRef) Room() (int32, bool) {
	rest, ok := strings.CutPrefix(string(r), conduitRoomTag)
	if !ok {
		return 0, false
	}
	id, err := strconv.ParseInt(rest, 10, 32)
	return int32(id), err == nil && id > 0
}

// Site is the node id of a site ref.
func (r ConduitRef) Site() (string, bool) {
	rest, ok := strings.CutPrefix(string(r), conduitSiteTag)
	return rest, ok && rest != ""
}

// ConduitLink is one laid conduit path: a node joined to the net at the cell
// of another. Cells run from the From node's cells to the first cell of the
// net, 4-connected, ends included. Paths share cells where they meet.
type ConduitLink struct {
	From, To ConduitRef
	Cells    []domain.Cell
}

// Conduit decisions; the caller journals them with telemetry.Decide.
const (
	ReasonConduitRouted   = "conduit_routed"
	ReasonConduitKept     = "conduit_kept"
	ReasonConduitBlocked  = "conduit_no_route"
	ReasonConduitNoSource = "conduit_no_source"
)

// ConduitDecision is one conduit decision for the caller to journal. To is
// empty when the node found no net to join.
type ConduitDecision struct {
	From, To ConduitRef
	Verdict  string
	Reason   string
}

// powerSiteKinds are the reservations that stand on the power net: the
// geothermal enclosures and the battery room. The turbines and solar plots are
// stored sites (powerSiteNodes).
var powerSiteKinds = []ReservationKind{ReserveBatteryRoom, ReserveGeothermal}

// ReservationSiteID names reservation index i as a conduit site: the kind and
// its position in LayoutPlan.Reservations.
func ReservationSiteID(kind ReservationKind, i int) string {
	return string(kind) + "-" + strconv.Itoa(i)
}

// powerSiteNodes are the stored turbines and solar plots, in plan order.
func (p LayoutPlan) powerSiteNodes() []PlannedSite {
	return append(p.LiveSites(NodeTurbine), p.LiveSites(NodeSolar)...)
}

// conduitSites are the power site node ids the plan holds.
func (p LayoutPlan) conduitSites() map[string]bool {
	out := map[string]bool{}
	for _, s := range p.powerSiteNodes() {
		out[s.Node] = true
	}
	for i, r := range p.Reservations {
		if slices.Contains(powerSiteKinds, r.Kind) {
			out[ReservationSiteID(r.Kind, i)] = true
		}
	}
	return out
}

// validConduits reports the conduit links as well formed: both ends name a
// room or a power site of the plan, differ, and the cells are a non-empty
// 4-connected path with no repeat.
func (p LayoutPlan) validConduits(rooms map[int32]Rectangle) bool {
	sites := p.conduitSites()
	known := func(r ConduitRef) bool {
		if id, ok := r.Room(); ok {
			_, found := rooms[id]
			return found
		}
		node, ok := r.Site()
		return ok && sites[node]
	}
	for _, l := range p.Conduits {
		if l.From == l.To || !known(l.From) || !known(l.To) || len(l.Cells) == 0 {
			return false
		}
		seen := map[domain.Cell]bool{}
		for i, c := range l.Cells {
			if seen[c] || i > 0 && abs32(c.X-l.Cells[i-1].X)+abs32(c.Z-l.Cells[i-1].Z) != 1 {
				return false
			}
			seen[c] = true
		}
	}
	return true
}

// conduitNode is a thing the net joins: its ref and the cells it stands on.
type conduitNode struct {
	ref   ConduitRef
	cells []domain.Cell
}

// conduitNet is the net as it grows: the cells the router may use, the cells
// laid, who laid each, and the links the colony already holds (heldLinks).
type conduitNet struct {
	allowed map[domain.Cell]bool
	net     map[domain.Cell]bool
	owner   map[domain.Cell]ConduitRef
	held    map[ConduitRef]ConduitLink
}

// stand puts a node's own cells into the net, where no earlier node laid them.
func (g *conduitNet) stand(n conduitNode) {
	for _, c := range n.cells {
		if !g.net[c] {
			g.net[c], g.owner[c] = true, n.ref
		}
	}
}

// keeps reports the held link of n as still good: it starts on n, every cell
// is ground a conduit may cross, and it ends on the net so far. A link that
// holds is the path already built or ordered, so a change elsewhere in the
// plan does not move it.
func (g *conduitNet) keeps(n conduitNode, l ConduitLink) bool {
	if len(l.Cells) == 0 || !slices.Contains(n.cells, l.Cells[0]) || !g.net[l.Cells[len(l.Cells)-1]] {
		return false
	}
	for _, c := range l.Cells {
		if !g.allowed[c] {
			return false
		}
	}
	return true
}

// lay joins node n to the net: its held link where that still holds, else the
// shortest path to the net. ok is false for a node the net cannot reach.
func (g *conduitNet) lay(n conduitNode) (ConduitLink, ConduitDecision, bool) {
	link, reason := g.held[n.ref], ReasonConduitKept
	if !g.keeps(n, link) {
		path := routeCells(n.cells, g.net, g.allowed)
		if len(path) == 0 {
			return ConduitLink{}, ConduitDecision{From: n.ref, Verdict: TileUnsatisfied, Reason: ReasonConduitBlocked}, false
		}
		slices.Reverse(path) // from the node to the net
		link, reason = ConduitLink{Cells: path}, ReasonConduitRouted
	}
	link.From = n.ref
	link.To = g.owner[link.Cells[len(link.Cells)-1]]
	for _, c := range link.Cells {
		if !g.net[c] {
			g.net[c], g.owner[c] = true, n.ref
		}
	}
	g.stand(n)
	return link, ConduitDecision{From: n.ref, To: link.To, Verdict: TilePlace, Reason: reason}, true
}

// newConduitNet is the ground the plan's nodes may route over: the survey's
// conduit cells, and each node's own cells, which are the plan's ground
// whatever the survey holds there.
func newConduitNet(plan LayoutPlan, nodes []conduitNode, survey MapSurvey, held map[ConduitRef]ConduitLink) *conduitNet {
	g := &conduitNet{allowed: ConduitCells(survey, plan.Rooms), net: map[domain.Cell]bool{}, owner: map[domain.Cell]ConduitRef{}, held: held}
	for _, n := range nodes {
		for _, c := range n.cells {
			g.allowed[c] = true
		}
	}
	return g
}

// siteNodes are the power sites of the plan as net nodes, in plan order.
func (p LayoutPlan) siteNodes() []conduitNode {
	var nodes []conduitNode
	for _, s := range p.powerSiteNodes() {
		cells := s.Footprint.Cells()
		sortCells(cells)
		nodes = append(nodes, conduitNode{ref: SiteRef(s.Node), cells: cells})
	}
	for i, r := range p.Reservations {
		if slices.Contains(powerSiteKinds, r.Kind) {
			cells := r.Area.Cells()
			sortCells(cells)
			nodes = append(nodes, conduitNode{ref: SiteRef(ReservationSiteID(r.Kind, i)), cells: cells})
		}
	}
	return nodes
}

// heldLinks are the links prev holds, by their From end, for the plan to keep
// where they still hold (#2966). A link of a room the plan replaces, or of a
// site it replaces, is the successor's: the successor joins the net by the path
// its predecessor's power runs on, so a replaced room keeps its conduit.
func heldLinks(prev []ConduitLink, plan LayoutPlan) map[ConduitRef]ConduitLink {
	held := map[ConduitRef]ConduitLink{}
	for _, l := range prev {
		held[l.From] = l
	}
	for _, r := range plan.Rooms {
		if l, ok := held[RoomRef(r.Replaces)]; ok && r.Replaces != 0 {
			if _, own := held[RoomRef(r.ID)]; !own {
				held[RoomRef(r.ID)] = l
			}
		}
	}
	for _, s := range plan.Sites {
		for _, old := range s.ReplacesSites {
			if l, ok := held[SiteRef(old)]; ok {
				if _, own := held[SiteRef(s.Node)]; !own {
					held[SiteRef(s.Node)] = l
				}
			}
		}
	}
	return held
}

// RouteConduits lays the plan's conduit net. sites are the power
// reservations (in reservation order) and consumers the rooms holding a
// station that draws power, with the cells those stations stand on (the whole
// room where none are set). prev are the links the colony holds: a node whose
// held link still holds keeps it (conduit_kept) and only the nodes it no
// longer serves route anew, so a changed wall or room moves the conduits it
// touches and no other. The result is deterministic in its inputs.
func RouteConduits(plan LayoutPlan, consumers []conduitNode, survey MapSurvey, prev []ConduitLink) ([]ConduitLink, []ConduitDecision) {
	nodes := plan.siteNodes()
	if len(nodes) == 0 {
		var decisions []ConduitDecision
		for _, n := range consumers {
			decisions = append(decisions, ConduitDecision{From: n.ref, Verdict: TileUnsatisfied, Reason: ReasonConduitNoSource})
		}
		return nil, decisions
	}
	nodes = append(nodes, consumers...)
	g := newConduitNet(plan, nodes, survey, heldLinks(prev, plan))
	g.stand(nodes[0])
	var links []ConduitLink
	var decisions []ConduitDecision
	for _, n := range nodes[1:] {
		link, d, ok := g.lay(n)
		if ok {
			links = append(links, link)
		}
		decisions = append(decisions, d)
	}
	return links, decisions
}

// JoinConduits joins consumers to the net the plan already holds (plan.Conduits
// and its power sites): the rooms a replan keeps that the tiler did not plan.
// Each keeps the link prev held where it still holds, else routes to the
// nearest cell of the net. It returns the plan's links with theirs and one
// decision per consumer.
func JoinConduits(plan LayoutPlan, consumers []conduitNode, survey MapSurvey, prev []ConduitLink) ([]ConduitLink, []ConduitDecision) {
	sites := plan.siteNodes()
	g := newConduitNet(plan, append(slices.Clone(sites), consumers...), survey, heldLinks(prev, plan))
	for _, n := range sites {
		g.stand(n)
	}
	for _, l := range plan.Conduits {
		for _, c := range l.Cells {
			if !g.net[c] {
				g.net[c], g.owner[c] = true, l.From
			}
		}
	}
	links := slices.Clone(plan.Conduits)
	var decisions []ConduitDecision
	for _, n := range consumers {
		link, d, ok := g.lay(n)
		if ok {
			links = append(links, link)
		}
		decisions = append(decisions, d)
	}
	return links, decisions
}

// routeTileConduits plans the conduit net over the furnished plan and records
// it. Consumers are the planned rooms holding a station whose def draws power
// (Station.BaseW < 0); a room's cells are those stations' footprints, or the
// whole room while the placer set none.
func (t *tiler) routeTileConduits() {
	var consumers []conduitNode
	for i, room := range t.out.Plan.Rooms {
		if i >= len(t.rooms) {
			break // the airlocks follow the group rooms and draw nothing
		}
		g := t.in.Groups[t.rooms[i].group]
		draws := map[string]bool{}
		for _, s := range g.Stations {
			if st, ok := t.in.Catalog.Station(s.Def); ok && st.DrawsPower() {
				draws[s.Def] = true
			}
		}
		if len(draws) == 0 {
			continue
		}
		var cells []domain.Cell
		for _, p := range t.out.Pieces[room.ID] {
			if draws[p.Def] {
				cells = append(cells, RectRegion(p.Rect).Cells()...)
			}
		}
		if len(cells) == 0 {
			cells = room.Region.Cells()
		}
		sortCells(cells)
		cells = slices.Compact(cells)
		consumers = append(consumers, conduitNode{ref: RoomRef(room.ID), cells: cells})
	}
	links, decisions := RouteConduits(t.out.Plan, consumers, t.in.Survey, t.in.Conduits)
	t.out.Plan.Conduits = links
	t.out.ConduitDecisions = decisions
}
