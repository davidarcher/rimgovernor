package policy

import (
	"container/heap"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Vanilla pathfinder prices the corridor is laid out against (#619):
// PathFinderJob's move ticks, the Fence def's pathCost, and
// Building_Door.TicksToOpenNow (45 / DoorOpenSpeed, 1.2 for wood) which
// PathUtility.GetDoorCost charges a pawn that can open the door. A spike
// trap has no pathCost, and the player's own pawns get no avoid grid, so
// colonists cross their own traps for free unless the layout prices the
// trap lane above the safe lane for them.
const (
	pathMoveCardinal = 13
	pathMoveDiagonal = 18
	pathFenceCost    = 80
	pathWoodDoorCost = 38
)

// corridorCosts is the layout's effect on colonist pathing: walls close a
// cell, fences and doors add their entry cost, doors (full-fill edifices)
// also forbid the diagonal steps that cut their corner, and traps mark the
// cells no cheapest colonist route may enter.
type corridorCosts struct {
	closed, full, traps map[domain.Cell]bool
	cost                map[domain.Cell]int
}

func newCorridorCosts() corridorCosts {
	return corridorCosts{closed: map[domain.Cell]bool{}, full: map[domain.Cell]bool{}, traps: map[domain.Cell]bool{}, cost: map[domain.Cell]int{}}
}

// stepCost is what entering c costs a colonist: the base move plus the
// cell's fence or door, or -1 when the step is impossible. An observed
// colony door prices like the layout's own.
func (s defenseSite) stepCost(k corridorCosts, from, to domain.Cell) int {
	if k.closed[to] || !s.passable(to) {
		return -1
	}
	move := pathMoveCardinal
	if from.X != to.X && from.Z != to.Z {
		move = pathMoveDiagonal
		for _, corner := range []domain.Cell{{X: to.X, Z: from.Z}, {X: from.X, Z: to.Z}} {
			if k.closed[corner] || !s.passable(corner) || k.full[corner] || positive(s.cells[corner].Door) {
				return -1
			}
		}
	}
	extra, priced := k.cost[to]
	if !priced && positive(s.cells[to].Door) {
		extra = pathWoodDoorCost
	}
	return move + extra
}

var pathSteps = [8]domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}, {X: 1, Z: 1}, {X: 1, Z: -1}, {X: -1, Z: -1}, {X: -1, Z: 1}}

type cellDistance struct {
	cell domain.Cell
	dist int
}
type cellQueue []cellDistance

func (q cellQueue) Len() int                   { return len(q) }
func (q cellQueue) Less(i, j int) bool         { return q[i].dist < q[j].dist }
func (q cellQueue) Swap(i, j int)              { q[i], q[j] = q[j], q[i] }
func (q *cellQueue) Push(x any)                { *q = append(*q, x.(cellDistance)) }
func (q *cellQueue) Pop() any                  { old := *q; x := old[len(old)-1]; *q = old[:len(old)-1]; return x }
func (q *cellQueue) push(c domain.Cell, d int) { heap.Push(q, cellDistance{c, d}) }

// distances runs Dijkstra from the sources. Forward, an edge from a to b
// costs stepCost(a, b); reversed, the same edge is relaxed from b back to
// a, so the result is the cost of the remaining route to a source.
func (s defenseSite) distances(k corridorCosts, sources []domain.Cell, reverse bool) map[domain.Cell]int {
	dist := map[domain.Cell]int{}
	q := &cellQueue{}
	for _, c := range sources {
		if s.passable(c) && !k.closed[c] {
			dist[c] = 0
			q.push(c, 0)
		}
	}
	for q.Len() > 0 {
		at := heap.Pop(q).(cellDistance)
		if at.dist > dist[at.cell] {
			continue
		}
		for _, d := range pathSteps {
			n := addCell(at.cell, d)
			var step int
			if reverse {
				step = s.stepCost(k, n, at.cell)
			} else {
				step = s.stepCost(k, at.cell, n)
			}
			if step < 0 {
				continue
			}
			if known, seen := dist[n]; !seen || at.dist+step < known {
				dist[n] = at.dist + step
				q.push(n, at.dist+step)
			}
		}
	}
	return dist
}

// colonistRouteAvoidsTraps reports whether a colonist starting at start
// still reaches the map edge once the layout stands, and whether every
// route the game's pathfinder could pick as cheapest stays off the trap
// cells: a trap lies on some cheapest route exactly when the cost to reach
// it plus the cost from it to the edge equals the best route cost. A tie
// counts as a crossing, since A* breaks ties by expansion order.
func (s defenseSite) colonistRouteAvoidsTraps(k corridorCosts, start domain.Cell) bool {
	var goals []domain.Cell
	for c, row := range s.cells {
		if positive(row.EdgeReachable) && s.onBorder(c) {
			goals = append(goals, c)
		}
	}
	forward := s.distances(k, []domain.Cell{start}, false)
	best := math.MaxInt
	for _, g := range goals {
		if d, ok := forward[g]; ok && d < best {
			best = d
		}
	}
	if best == math.MaxInt {
		return false
	}
	backward := s.distances(k, goals, true)
	for t := range k.traps {
		to, reached := forward[t]
		from, leaves := backward[t]
		if reached && leaves && to+from == best {
			return false
		}
	}
	return true
}
