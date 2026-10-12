package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The power sites as site nodes of the station graph (#2919, #2960). The chain
// asks for one node per turbine, solar plot and geothermal enclosure
// (PowerSiteNodes) and EmbedSites places them; the tiler stores the turbines
// and plots as PlannedSites (NodeTurbine, NodeSolar) with their wind lanes, and
// PlannedPowerSites, TurbinePlacement and TurbineCatchZone stay the read API.
//
//   - A turbine is a small footprint with a wind lane and four turns. The lane
//     overlaps fields and other lanes freely: crops do not block wind, so a lane
//     over a field costs nothing, and a lane over another lane costs nothing.
//     Only what the game counts costs (windGround): another turbine's footprint
//     in the lane is a blocker like any building. Pairing, spacing and facing
//     are whatever minimises that loss, not a rule.
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

// turbineTurns are a turbine's four turns, with whether the turbine is the
// pair's southern one in TurbinePlacement's terms and whether it lies turned.
type turbineTurn struct {
	rot           domain.Rotation
	south, turned bool
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
		centre, rot := TurbinePlacement(pad, t.south)
		lane := SiteLane{ID: "wind", Region: RegionOf(TurbineWindCells(centre, rot)), Reason: ReasonWindLane, Wind: true}
		shapes = append(shapes, SiteNode{ID: id, Footprint: RectRegion(pad), Lanes: []SiteLane{lane}, Rotation: rot})
	}
	n := shapes[0]
	n.Alts, n.Kind, n.Reason = shapes[1:], NodeTurbine, ReasonPowerSite
	n.Band = DistanceBand{Max: powerSiteReach}
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

// The power sites a plan is owed, by generator def: turbines stand singly (a
// facing pair falls out of the wind-lane pricing, not a rule), and a plan owes
// the two and the one plot the old generator reserved, the day-1 supply
// (judgment: nothing in the game says how many a first plan holds; the colony's
// demand grows the rest through SelectPowerMethod, which builds on the planned
// sites). Registered in DefTables (powerSites).
var firstPowerSites = map[string]int{WindTurbineDefinition: 2, SolarDefinition: 1}

// powerSiteReach is the most clear ground, in cells, a turbine or solar plot
// keeps from the nearest room when one stands in reach (judgment: the ring
// encloses the sites, so ground far from the rooms drags the wall out and
// the old generator kept them against the core; the embedder falls back to
// any ground when none lies in reach). Registered with firstPowerSites.
const powerSiteReach int32 = 6

// Node kinds of the stored power sites.
const (
	NodeTurbine NodeKind = "wind_turbine"
	NodeSolar   NodeKind = "solar"
)

// PowerWants is how many turbine and solar nodes the graph holds.
type PowerWants struct{ Turbines, Solar int }

// FirstPowerWants is what a first plan owes (firstPowerSites).
func FirstPowerWants() PowerWants {
	return PowerWants{Turbines: firstPowerSites[WindTurbineDefinition], Solar: firstPowerSites[SolarDefinition]}
}

// PowerSiteNodes are the site nodes for the plan's power sites: a node per
// turbine, solar plot and geothermal enclosure, ids numbered in plan order.
// The plan's own sites count, and want raises the counts for a plan that holds
// fewer (a first plan holds none).
func PowerSiteNodes(plan LayoutPlan, want PowerWants) []SiteNode {
	var nodes []SiteNode
	for i := range max(len(PlannedPowerSites(plan, WindTurbineDefinition)), want.Turbines) {
		nodes = append(nodes, turbineNode(fmt.Sprintf("wind-%d", i+1)))
	}
	for i := range max(len(PlannedPowerSites(plan, SolarDefinition)), want.Solar) {
		nodes = append(nodes, SiteNode{ID: fmt.Sprintf("solar-%d", i+1), Kind: NodeSolar, Footprint: RectRegion(Rectangle{Width: solarSide, Height: solarSide}), Band: DistanceBand{Max: powerSiteReach}, Reason: ReasonPowerSite})
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
// counts blocked. A cell is blocked off the map, under a thick mountain roof,
// in a fenced room (its roof and walls) or under a placed footprint.
type windGround struct {
	bounds Bounds
	cells  map[domain.Cell]bool
}

func newWindGround(in SiteEmbedInput) *windGround {
	g := &windGround{bounds: in.Embed.Survey.Bounds, cells: map[domain.Cell]bool{}}
	for _, c := range in.Embed.Survey.Cells {
		if c.ThickRoof {
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
