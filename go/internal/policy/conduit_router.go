package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// powerRouteBound caps the cells one route search visits: enough to reach a
// geyser GeothermalReachCells away across open ground, or to cross a
// map-wide perimeter interior to a moisture pump.
const powerRouteBound = 1 << 17

// powerRoute is the shortest 4-neighbour path from start over allowed cells to
// the first destination reached. See routeCells.
func powerRoute(start domain.Cell, destinations, allowed map[domain.Cell]bool) []domain.Cell {
	return routeCells([]domain.Cell{start}, destinations, allowed)
}

// routeCells is the shared deterministic conduit router: a breadth-first
// search from every allowed start at once over 4-neighbour steps in a fixed
// order, so the nearest destination to any start wins and ties go to the
// earlier start and the east, west, south, north neighbour order. The path
// runs destination first and start last: the generator-to-consumer order lets
// successive eight-cell methods extend one route. Unknown terrain and
// protected cells never become routes (they are not in allowed). Nil means no
// route, or more than powerRouteBound cells visited.
func routeCells(starts []domain.Cell, destinations, allowed map[domain.Cell]bool) []domain.Cell {
	previous := map[domain.Cell]domain.Cell{}
	var queue []domain.Cell
	for _, s := range starts {
		if _, seen := previous[s]; allowed[s] && !seen {
			previous[s] = s
			queue = append(queue, s)
		}
	}
	for i := 0; i < len(queue); i++ {
		c := queue[i]
		if destinations[c] {
			path := []domain.Cell{c}
			for previous[c] != c {
				c = previous[c]
				path = append(path, c)
			}
			return path
		}
		for _, next := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if _, found := previous[next]; allowed[next] && !found {
				if len(previous) >= powerRouteBound {
					return nil
				}
				previous[next] = c
				queue = append(queue, next)
			}
		}
	}
	return nil
}

// ConduitCells are the cells a power conduit may run over on this survey: any
// surveyed cell whose terrain takes a light structure. Buildings, walls and
// doors do not block (PowerConduit sits on the Conduits layer; only another
// power transmitter conflicts), nor do trees or ruins. Deep water and other
// terrain without footing, hazards (lava) and natural rock block; rock inside
// the walls of a dug room does not, since the room is mined out. Cells the
// survey does not hold are unknown and never routed.
func ConduitCells(survey MapSurvey, rooms []PlannedRoom) map[domain.Cell]bool {
	dug := map[domain.Cell]bool{}
	for _, r := range rooms {
		if !r.Dug {
			continue
		}
		for _, c := range RectRegion(roomWalls(r)).Cells() {
			dug[c] = true
		}
	}
	out := make(map[domain.Cell]bool, len(survey.Cells))
	for _, c := range survey.Cells {
		if c.Footing == FootingNone || c.Hazard || c.Rock && !dug[c.Cell] {
			continue
		}
		out[c.Cell] = true
	}
	return out
}
