package combatsim

import (
	"container/heap"
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func unknownPawn(id domain.PawnID) error {
	return fmt.Errorf("combatsim: order for unknown pawn %q", id)
}

// search is the reusable A* state. Cells are stamped with a generation so a
// search never clears the grid-sized arrays.
type search struct {
	gen    uint32
	seen   []uint32 // generation a cell's gScore/from were set
	closed []uint32 // generation a cell was expanded
	gScore []float64
	from   []int32
	open   openList
	out    []int32
}

type node struct {
	f float64
	i int32
}

// openList orders by f, then cell index, so equal-cost paths resolve the same
// way on every run.
type openList []node

func (h openList) Len() int { return len(h) }
func (h openList) Less(a, b int) bool {
	if h[a].f != h[b].f {
		return h[a].f < h[b].f
	}
	return h[a].i < h[b].i
}
func (h openList) Swap(a, b int) { h[a], h[b] = h[b], h[a] }
func (h *openList) Push(x any)   { *h = append(*h, x.(node)) }
func (h *openList) Pop() any {
	old := *h
	n := old[len(old)-1]
	*h = old[:len(old)-1]
	return n
}

func (sr *search) reset(n int) {
	if len(sr.seen) != n {
		sr.seen = make([]uint32, n)
		sr.closed = make([]uint32, n)
		sr.gScore = make([]float64, n)
		sr.from = make([]int32, n)
		sr.gen = 0
	}
	sr.gen++
	sr.open = sr.open[:0]
}

// findPath is the pathfinder: A* over the eight neighbours priced by the
// pawn's own per-cell cost (stepCost), a diagonal step allowed only when both
// cardinal cells beside it are walkable. It returns the cells to enter after
// the pawn's position, valid until the next call. Ties break by cell index.
func (s *Sim) findPath(p *pawn, to domain.Cell) ([]int32, bool) {
	g := &s.g
	sr := &s.search
	sr.reset(g.w * g.h)
	start, goal := g.idx(p.pos), g.idx(to)

	// Cheapest possible cardinal and diagonal step, for an admissible heuristic.
	card, diag := s.cheapestTicks(p.speed)
	h := func(c domain.Cell) float64 {
		dx, dz := float64(abs32(c.X-to.X)), float64(abs32(c.Z-to.Z))
		return card*(math.Max(dx, dz)-math.Min(dx, dz)) + diag*math.Min(dx, dz)
	}

	sr.seen[start] = sr.gen
	sr.gScore[start] = 0
	sr.from[start] = -1
	heap.Push(&sr.open, node{f: h(p.pos), i: int32(start)})
	for sr.open.Len() > 0 {
		cur := heap.Pop(&sr.open).(node)
		ci := int(cur.i)
		if sr.closed[ci] == sr.gen {
			continue
		}
		sr.closed[ci] = sr.gen
		if ci == goal {
			return sr.trace(goal), true
		}
		cc := g.cell(ci)
		for k, off := range adjacent {
			nb := add(cc, off)
			if !g.walkable(nb) {
				continue
			}
			if k >= 4 && (!g.walkable(domain.Cell{X: nb.X, Z: cc.Z}) || !g.walkable(domain.Cell{X: cc.X, Z: nb.Z})) {
				continue
			}
			ni := g.idx(nb)
			if sr.closed[ni] == sr.gen {
				continue
			}
			gs := sr.gScore[ci] + s.stepCost(p.speed, cc, nb)
			if sr.seen[ni] == sr.gen && gs >= sr.gScore[ni] {
				continue
			}
			sr.seen[ni] = sr.gen
			sr.gScore[ni] = gs
			sr.from[ni] = int32(ci)
			heap.Push(&sr.open, node{f: gs + h(nb), i: int32(ni)})
		}
	}
	return nil, false
}

// cheapestTicks is the lowest cardinal and diagonal ticksPerMove over roofed
// and unroofed cells, so the heuristic never overestimates.
func (s *Sim) cheapestTicks(speed float64) (card, diag float64) {
	if speed == 0 {
		return maxTicksPerMove, maxTicksPerMove
	}
	t := 60 / speed
	t = math.Min(t, t/s.weather)
	clamp := func(v float64) float64 { return math.Min(math.Max(v, 1), maxTicksPerMove) }
	return clamp(t), clamp(t * diagonalFactor)
}

// trace walks the parents back from goal and returns the path start-exclusive.
func (sr *search) trace(goal int) []int32 {
	sr.out = sr.out[:0]
	for i := int32(goal); sr.from[i] >= 0; i = sr.from[i] {
		sr.out = append(sr.out, i)
	}
	for a, b := 0, len(sr.out)-1; a < b; a, b = a+1, b-1 {
		sr.out[a], sr.out[b] = sr.out[b], sr.out[a]
	}
	return sr.out
}
