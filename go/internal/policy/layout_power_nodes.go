package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The power sites as site nodes of the station graph (#2919, #2960, #2999). The
// chain asks for one node per turbine, solar plot and geothermal enclosure
// (PowerSiteNodes, derivePowerSites) and the site embedder places them; the
// tiler stores the turbines and plots as PlannedSites (NodeTurbine, NodeSolar)
// with their wind lanes, and PlannedPowerSites, TurbinePlacement and
// TurbineCatchZone stay the read API.
//
//   - A turbine is a small footprint with a wind lane and four turns. The lane
//     overlaps fields and other lanes freely: crops do not block wind, so a lane
//     over a field costs nothing, and a lane over another lane costs nothing.
//     Only what the game counts costs (windGround): a tree, a roof, a wall and
//     another turbine's footprint in the lane are blockers like any building.
//     Pairing, spacing and facing are whatever minimises the lost watts, not a
//     rule.
//   - Solar is a free plot.
//   - Geothermal is anchored on its geyser's enclosure.
//   - The battery room is a planned room (PlannedBattery) and stays one.

// The wind path is a game rule, read with ilspycmd from CompPowerPlantWind and
// WindTurbineUtility.CalculateWindCells and registered in DefTables under the
// WindTurbine def: the path is windPathWidth wide, windPathAhead rows in front
// starting windPathStart out from the centre and windPathBehind behind (the
// turbine's own cells are not in it, so it never blocks itself). Each path cell
// that is roofed, off the map or holds a thing whose def has blockWind removes
// windBlockedLoss of the output; five or more remove all of it.
const (
	windPathWidth   int32 = 7
	windPathAhead   int32 = 10
	windPathStart   int32 = 2
	windPathBehind  int32 = 6
	windBlockedLoss       = 0.2
)

// WindOutput is the fraction of its rated output a turbine makes with blocked
// wind-path cells.
func WindOutput(blocked int) float64 {
	return max(1-windBlockedLoss*float64(blocked), 0)
}

// ReasonPowerSite is why the graph holds a power site node.
const ReasonPowerSite = "planned_power_site"

// ReasonWindLane is why a turbine carries a wind lane.
const ReasonWindLane = "wind_path"

// turbineTurns are a turbine's four turns, with whether the turbine stands on
// its pad's low row and faces the rising axis (TurbinePlacement's lowRow: north
// or east) and whether the pad lies turned.
type turbineTurn struct {
	rot            domain.Rotation
	lowRow, turned bool
}

var turbineTurns = []turbineTurn{
	{domain.North, true, false}, {domain.South, false, false}, {domain.East, true, true}, {domain.West, false, true},
}

// turbineNode is turbine i: its footprint is the 7x2 pad of a planned turbine
// (2x7 turned) and the wind lane the path of its centre.
func turbineNode(id string) SiteNode {
	var shapes []SiteNode
	for _, t := range turbineTurns {
		pad := Rectangle{Width: windPathWidth, Height: 2}
		if t.turned {
			pad = Rectangle{Width: 2, Height: windPathWidth}
		}
		centre, rot := TurbinePlacement(pad, t.lowRow)
		lane := SiteLane{ID: "wind", Region: RegionOf(TurbineWindCells(centre, rot)), Reason: ReasonWindLane, Wind: true}
		shapes = append(shapes, SiteNode{ID: id, Footprint: RectRegion(pad), Lanes: []SiteLane{lane}, Rotation: rot})
	}
	n := shapes[0]
	n.Alts, n.Kind, n.Reason = shapes[1:], NodeTurbine, ReasonPowerSite
	return n
}

// PowerSitesOf are the placed turbines and solar plots as the plan's stored
// sites, in node order. A geothermal enclosure is anchored on the reservation
// the plan already holds and a battery room is a room, so neither is stored
// again; a node that found no ground (no placement) is left out.
func PowerSitesOf(nodes []SiteNode, placed SiteEmbedding) []PlannedSite {
	var out []PlannedSite
	for _, n := range nodes {
		if n.Kind != NodeTurbine && n.Kind != NodeSolar {
			continue
		}
		if p, ok := placed.Site(n.ID); ok {
			out = append(out, PlannedSite{SitePlacement: p, Kind: n.Kind, Reason: n.Reason})
		}
	}
	return out
}

// Node kinds of the stored power sites.
const (
	NodeTurbine NodeKind = "wind_turbine"
	NodeSolar   NodeKind = "solar"
)

// windNodeID and solarNodeID are the ids of the i-th (from 1) turbine and solar
// plot node.
func windNodeID(i int) string  { return fmt.Sprintf("wind-%d", i) }
func solarNodeID(i int) string { return fmt.Sprintf("solar-%d", i) }

// solarNode is solar plot i: a free square.
func solarNode(id string) SiteNode {
	return SiteNode{ID: id, Kind: NodeSolar, Footprint: RectRegion(Rectangle{Width: solarSide, Height: solarSide}), Reason: ReasonPowerSite}
}

// PowerSiteNodes are the site nodes for the plan's power sites: a node per
// turbine, solar plot and geothermal enclosure, ids numbered in plan order. A
// plan holds the sites it was grown with; a first plan derives how many it
// owes from the demand it plans for (derivePowerSites).
func PowerSiteNodes(plan LayoutPlan) []SiteNode {
	var nodes []SiteNode
	for i := range len(PlannedPowerSites(plan, WindTurbineDefinition)) {
		nodes = append(nodes, turbineNode(windNodeID(i+1)))
	}
	for i := range len(PlannedPowerSites(plan, SolarDefinition)) {
		nodes = append(nodes, solarNode(solarNodeID(i+1)))
	}
	i := 0
	for _, r := range plan.Reservations {
		if r.Kind != ReserveGeothermal {
			continue
		}
		i++
		area := r.Area.Bounds()
		anchor := domain.Cell{X: area.X, Z: area.Z}
		nodes = append(nodes, SiteNode{ID: geothermalNodeID(i), Footprint: RectRegion(Rectangle{Width: area.Width, Height: area.Height}), Anchor: &anchor, Reason: ReasonPowerSite})
	}
	return nodes
}

// geothermalNodeID is the id of the i-th (from 1) geothermal enclosure node, in
// plan order; the defense record names the enclosure's tier by it.
func geothermalNodeID(i int) string { return fmt.Sprintf("geothermal-%d", i) }

// windGround is the ground a wind path is priced over: the cells the game
// counts blocked (CompPowerPlantWind.UpdateDesiredPowerOutput: a roofed cell or
// a thing whose def has blockWind). A cell is blocked off the map, under a thick
// mountain roof, under a tree (TreeBase sets blockWind; crops and bushes do
// not), in a fenced room (its roof and walls) or under a placed footprint (the
// turbine def sets blockWind).
type windGround struct {
	bounds Bounds
	cells  map[domain.Cell]bool
}

func newWindGround(in SiteEmbedInput) *windGround {
	g := &windGround{bounds: in.Embed.Survey.Bounds, cells: map[domain.Cell]bool{}}
	for _, c := range in.Embed.Survey.Cells {
		if c.ThickRoof || c.Tree {
			g.cells[c.Cell] = true
		}
	}
	for _, b := range in.Embedding.Blocks {
		// An open-air room (the range, the ring) has no roof and no walls.
		if role, ok := PlannedRoleOfGroup(in.Embed.Groups[b.Group]); ok && exactFloor(role) {
			continue
		}
		g.add(RectRegion(pad(b.Interior, 1)))
	}
	return g
}

func (g *windGround) add(r Region) {
	for _, c := range r.Cells() {
		g.cells[c] = true
	}
}

// blocked counts the lane's cells the game counts blocked, the node's own
// footprint excepted.
func (g *windGround) blocked(lane, own Region) int {
	n := 0
	for _, c := range lane.Cells() {
		if c.X < 0 || c.Z < 0 || c.X >= g.bounds.Width || c.Z >= g.bounds.Height || g.cells[c] && !own.Contains(c) {
			n++
		}
	}
	return n
}

// blockedAt is blocked for a lane shifted by (dx, dz), the node's own footprint
// a rectangle: the embedder prices a lane at every origin without translating it.
func (g *windGround) blockedAt(lane Region, dx, dz int32, own Rectangle) int {
	n := 0
	for _, run := range lane.Runs {
		z := run.Z + dz
		for x := run.X + dx; x < run.X+dx+run.Length; x++ {
			c := domain.Cell{X: x, Z: z}
			if x < 0 || z < 0 || x >= g.bounds.Width || z >= g.bounds.Height || g.cells[c] && !contains(own, c) {
				n++
			}
		}
	}
	return n
}
