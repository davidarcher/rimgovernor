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
// "site:<node id>" for a site. Until site nodes are planned (#2919), a power
// reservation is a site, named by SiteRef.
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
// generators and the battery room.
var powerSiteKinds = []ReservationKind{ReserveBatteryRoom, ReserveTurbine, ReserveSolar, ReserveGeothermal}

// ReservationSiteID names reservation index i as a site node until #2919
// re-points reservations to site nodes: the kind and its position in
// LayoutPlan.Reservations.
func ReservationSiteID(kind ReservationKind, i int) string {
	return string(kind) + "-" + strconv.Itoa(i)
}

// conduitSites are the power site node ids the plan holds.
func (p LayoutPlan) conduitSites() map[string]bool {
	out := map[string]bool{}
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

// RouteConduits lays the plan's conduit net. sites are the power
// reservations (in reservation order) and consumers the rooms holding a
// station that draws power, with the cells those stations stand on (the whole
// room where none are set). The result is deterministic in its inputs.
func RouteConduits(plan LayoutPlan, consumers []conduitNode, survey MapSurvey) ([]ConduitLink, []ConduitDecision) {
	allowed := ConduitCells(survey, plan.AllRooms())
	var nodes []conduitNode
	for i, r := range plan.Reservations {
		if slices.Contains(powerSiteKinds, r.Kind) {
			cells := r.Area.Cells()
			sortCells(cells)
			nodes = append(nodes, conduitNode{ref: SiteRef(ReservationSiteID(r.Kind, i)), cells: cells})
		}
	}
	sites := len(nodes)
	nodes = append(nodes, consumers...)
	var links []ConduitLink
	var decisions []ConduitDecision
	if sites == 0 {
		for _, n := range consumers {
			decisions = append(decisions, ConduitDecision{From: n.ref, Verdict: TileUnsatisfied, Reason: ReasonConduitNoSource})
		}
		return nil, decisions
	}
	// Each node's own cells stand in the router's reach: they are the plan's
	// ground, whatever the survey holds there.
	for _, n := range nodes {
		for _, c := range n.cells {
			allowed[c] = true
		}
	}
	owner := map[domain.Cell]ConduitRef{}
	net := map[domain.Cell]bool{}
	root := nodes[0]
	for _, c := range root.cells {
		net[c], owner[c] = true, root.ref
	}
	for _, n := range nodes[1:] {
		path := routeCells(n.cells, net, allowed)
		if len(path) == 0 {
			decisions = append(decisions, ConduitDecision{From: n.ref, Verdict: TileUnsatisfied, Reason: ReasonConduitBlocked})
			continue
		}
		slices.Reverse(path) // from the node to the net
		to := owner[path[len(path)-1]]
		links = append(links, ConduitLink{From: n.ref, To: to, Cells: path})
		decisions = append(decisions, ConduitDecision{From: n.ref, To: to, Verdict: TilePlace, Reason: ReasonConduitRouted})
		for _, c := range path {
			if !net[c] {
				net[c], owner[c] = true, n.ref
			}
		}
		for _, c := range n.cells {
			if !net[c] {
				net[c], owner[c] = true, n.ref
			}
		}
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
	links, decisions := RouteConduits(t.out.Plan, consumers, t.in.Survey)
	t.out.Plan.Conduits = links
	t.out.ConduitDecisions = decisions
}
