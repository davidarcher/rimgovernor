package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// buildGround is the survey's ground as the embedder and the tiler read it: the
// cells a room may stand on (the core candidates, ZoneCore), the rock a room
// there is dug into (ZoneMining), and each cell's soil cost. The other zones
// give way to the core wherever they overlap it.
type buildGround struct {
	core, rock map[domain.Cell]bool
	// soil is each surveyed cell's build cost; nil costs nothing.
	soil map[domain.Cell]int
}

// coreMaxDepth is the deepest room interior the siting seeds allow for.
const coreMaxDepth int32 = 7

// coreHalf is the half height of the cross-section a siting seed must have
// free about its row: a hallway cell, the deepest room and a wall on each side.
const coreHalf = 1 + coreMaxDepth + 2

// Soil build costs per cell: rich soil costs more than
// plain soil, which costs more than anything else (bare ground, rock).
// Rich soil (fertility above zoneRichFertility, e.g. 140%) is the best
// farmland on the map, so a room over it costs 50x plain soil: still a cost,
// not a ban, but only a site with no other ground pays it.
const (
	soilCostRich   = 100
	soilCostNormal = 2
	soilCostOther  = 0
)

// newBuildGround takes reserved sites out of the core candidates.
func newBuildGround(zones []LayoutZone, reserved []LayoutReservation) buildGround {
	g := buildGround{core: map[domain.Cell]bool{}, rock: map[domain.Cell]bool{}}
	for _, z := range zones {
		var set map[domain.Cell]bool
		switch z.Kind {
		case ZoneCore:
			set = g.core
		case ZoneMining:
			set = g.rock
		default:
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				set[domain.Cell{X: x, Z: r.Z}] = true
			}
		}
	}
	for _, r := range reserved {
		for _, c := range r.Area.Cells() {
			delete(g.core, c)
		}
	}
	return g
}

// withSoil gives g a per-cell build cost from the survey's fertility.
func (g buildGround) withSoil(s MapSurvey) buildGround {
	g.soil = surveySoil(s)
	return g
}

// surveySoil is each surveyed cell's build cost.
func surveySoil(s MapSurvey) map[domain.Cell]int {
	soil := make(map[domain.Cell]int, len(s.Cells))
	for _, c := range s.Cells {
		switch {
		case c.Rock:
			soil[c.Cell] = soilCostOther
		case c.Fertility > zoneRichFertility:
			soil[c.Cell] = soilCostRich
		case c.Fertility >= zoneFieldFertility:
			soil[c.Cell] = soilCostNormal
		default:
			soil[c.Cell] = soilCostOther
		}
	}
	return soil
}

// soilCost sums the build cost of r's cells.
func (g buildGround) soilCost(r Rectangle) int {
	n := 0
	for x := r.X; x < r.X+r.Width; x++ {
		for z := r.Z; z < r.Z+r.Height; z++ {
			n += g.soil[domain.Cell{X: x, Z: z}]
		}
	}
	return n
}

// column reports the whole cross-section at x around row z free: half a
// hallway plus the deepest room and its walls on both sides.
func (g buildGround) column(x, z int32) bool {
	half := coreHalf
	for dz := -half; dz <= half; dz++ {
		if !g.core[domain.Cell{X: x, Z: z + dz}] {
			return false
		}
	}
	return true
}

// seed picks the start of the siting: the column-fitting cell nearest the core
// candidates' centroid.
func (g buildGround) seed() (domain.Cell, bool) {
	var sx, sz int64
	for c := range g.core {
		sx += int64(c.X)
		sz += int64(c.Z)
	}
	n := int64(len(g.core))
	cx, cz := int32(sx/n), int32(sz/n)
	best, found, bestD := domain.Cell{}, false, int64(-1)
	for c := range g.core {
		dx, dz := int64(c.X-cx), int64(c.Z-cz)
		d := dx*dx + dz*dz
		if found && (d > bestD || d == bestD && (c.Z > best.Z || c.Z == best.Z && c.X > best.X)) {
			continue
		}
		// The seed is a hallway end: its one-cell cap needs core too.
		if g.column(c.X-1, c.Z) && g.column(c.X, c.Z) && g.column(c.X+1, c.Z) {
			best, found, bestD = c, true, d
		}
	}
	return best, found
}

// dug reports whether any interior cell of r is rock.
func (g buildGround) dug(r PlannedRoom) bool {
	in := r.Region.Bounds()
	for x := in.X; x < in.X+in.Width; x++ {
		for z := in.Z; z < in.Z+in.Height; z++ {
			if g.rock[domain.Cell{X: x, Z: z}] {
				return true
			}
		}
	}
	return false
}

// Obstacles are core cells a room should not stand on: rich soil, the ore the
// plan will not dig, the field zones (soil worth farming) and, already out of
// the core, the reserved sites. A rich patch inside the best ground is left
// open (a courtyard) rather than built over at soilCostRich. Obstacles are
// geometry first and cost second: obstacleLevels tries the strictest map, then
// fewer, and the last level keeps no obstacle at all, so the soil cost prices
// the plan when nothing else places every room.

// obstacleLevel names which cells are obstacles.
type obstacleLevel int

const (
	// obstacleAll: rich soil, ore rock and every field zone cell.
	obstacleAll obstacleLevel = iota
	// obstacleRich: rich soil and ore rock only; plain fields may be built on.
	obstacleRich
	// obstacleNone: no obstacle; rich soil is a cost (soilCostRich), not a ban.
	obstacleNone
)

// obstacleLevels is the order siting tries the levels in.
var obstacleLevels = []obstacleLevel{obstacleAll, obstacleRich, obstacleNone}

// coreObstacles are the cells of g.core at level that a room keeps off. zones
// supplies the field and ore cells; g.soil the rich cells.
func (g buildGround) coreObstacles(zones []LayoutZone, level obstacleLevel) map[domain.Cell]bool {
	return g.coreObstaclesFor(zones, level, true)
}

// coreObstaclesFor is coreObstacles with the field ban optional: fields are
// nodes of the station graph, placed by the embedder and sharing occupancy
// with the rooms (#2920), so the embedder asks for none.
func (g buildGround) coreObstaclesFor(zones []LayoutZone, level obstacleLevel, banFields bool) map[domain.Cell]bool {
	obs := map[domain.Cell]bool{}
	if level == obstacleNone {
		return obs
	}
	for c := range g.core {
		if g.soil[c] == soilCostRich {
			obs[c] = true
		}
	}
	for _, z := range zones {
		if z.Kind == ZoneMining && z.Ore || z.Kind == ZoneField && level == obstacleAll && banFields {
			for _, r := range z.Runs {
				for x := r.X; x < r.X+r.Length; x++ {
					if c := (domain.Cell{X: x, Z: r.Z}); g.core[c] {
						obs[c] = true
					}
				}
			}
		}
	}
	return obs
}

// without is a copy of g whose core lacks obs.
func (g buildGround) without(obs map[domain.Cell]bool) buildGround {
	core := make(map[domain.Cell]bool, len(g.core))
	for c, ok := range g.core {
		if !obs[c] {
			core[c] = ok
		}
	}
	g.core = core
	return g
}
