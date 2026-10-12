package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The power sites as site nodes of the station graph (#2919). The advisory
// chain asks for one node per planned turbine, solar plot and geothermal
// enclosure (PowerSiteNodes) and EmbedSites places them; PlanUtilities stays
// the authoritative planner until the cutover (#2931), and PlannedPowerSites,
// TurbinePlacement and TurbineCatchZone stay the read API.
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
var turbineTurns = []struct {
	rot           domain.Rotation
	south, turned bool
}{
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
	n.Alts, n.Reason = shapes[1:], ReasonPowerSite
	return n
}

// PowerSiteNodes are the site nodes for the plan's power sites: a node per
// planned turbine, solar plot and geothermal enclosure, ids numbered in plan
// order.
func PowerSiteNodes(plan LayoutPlan) []SiteNode {
	var nodes []SiteNode
	for i := range PlannedPowerSites(plan, WindTurbineDefinition) {
		nodes = append(nodes, turbineNode(fmt.Sprintf("wind-%d", i+1)))
	}
	for i := range PlannedPowerSites(plan, SolarDefinition) {
		nodes = append(nodes, SiteNode{ID: fmt.Sprintf("solar-%d", i+1), Footprint: RectRegion(Rectangle{Width: solarSide, Height: solarSide}), Reason: ReasonPowerSite})
	}
	i := 0
	for _, r := range plan.Reservations {
		if r.Kind != ReserveGeothermal {
			continue
		}
		i++
		area := r.Area.Bounds()
		anchor := domain.Cell{X: area.X, Z: area.Z}
		nodes = append(nodes, SiteNode{ID: fmt.Sprintf("geothermal-%d", i), Footprint: RectRegion(Rectangle{Width: area.Width, Height: area.Height}), Anchor: &anchor, Reason: ReasonPowerSite})
	}
	return nodes
}

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
