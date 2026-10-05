package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The new generator's obstacle map (#1955, epic #1938; absorbs #1921). The
// obstacles are core cells a room or hallway should not stand on: rich soil,
// the ore the plan will not dig, the field zones (soil worth farming) and,
// already out of the core, the reserved sites. the packer rejects any slot
// touching one, so a rich patch inside the best ground is left open (a
// courtyard) rather than built over at soilCostRich. Obstacles are geometry
// first and cost second: obstacleLevels tries the strictest map, then
// fewer, and the last level keeps no obstacle at all, so the soil cost
// prices the plan when nothing else places every base room.

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

// coreObstacles are the cells of g.core at level that a room or hallway
// keeps off. zones supplies the field and ore cells; g.soil the rich cells.
func (g coreGrid) coreObstacles(zones []LayoutZone, level obstacleLevel) map[domain.Cell]bool {
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
		if z.Kind == ZoneMining && z.Ore || z.Kind == ZoneField && level == obstacleAll {
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

// withObstacles is a copy of g whose core lacks obs, so slot search, the
// hallway and the wings all keep off them.
func (g coreGrid) withObstacles(obs map[domain.Cell]bool) coreGrid {
	c := g.clone()
	for cell := range obs {
		delete(c.core, cell)
	}
	return c
}
