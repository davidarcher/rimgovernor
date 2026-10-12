package policy

import (
	"math"
	"runtime"
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Siting seeds and the survey's shared ground state: the candidate seeds the
// embedder starts from, and the walking distances every score reads.

// Search the best siteSearchTop candidates for at most siteSearchIters moves
// each. Iteration counts keep results deterministic; hourly replans use a
// separate, smaller budget.
const (
	siteSearchTop   = 8
	siteSearchIters = 40
)

// siteSeeds is the candidate set, at most n seeds, every one passing
// column: the centroid seed, then an even share each of cells just
// outside a rich patch, cells along the rock edge and a coarse grid over
// the core ground, each stepped evenly through its (Z, X) order.
func (g buildGround) siteSeeds(n int) []domain.Cell {
	var out []domain.Cell
	seen := map[domain.Cell]bool{}
	add := func(c domain.Cell) {
		if !seen[c] {
			seen[c] = true
			out = append(out, c)
		}
	}
	if c, ok := g.seed(); ok {
		add(c)
	}
	var edge, rock, grid []domain.Cell
	dense := newDenseCore(g.core)
	anyRich := false // nearRich is false everywhere when no cell is rich
	for _, v := range g.soil {
		if v == soilCostRich {
			anyRich = true
			break
		}
	}
	for c := range g.core {
		if !dense.column(c.X, c.Z) { // g.column, read off a dense copy of the core
			continue
		}
		if anyRich && g.soil[c] != soilCostRich && g.nearRich(c) {
			edge = append(edge, c)
		}
		if !g.rock[c] && g.besideRock(c) {
			rock = append(rock, c)
		}
		if c.X%siteGridStep == 0 && c.Z%siteGridStep == 0 {
			grid = append(grid, c)
		}
	}
	share := max((n-1)/3, 1)
	for _, set := range [][]domain.Cell{edge, rock, grid} {
		sortCells(set)
		for _, c := range stepped(set, share) {
			add(c)
		}
	}
	return out[:min(n, len(out))]
}

// denseCore is a core set as a bool grid over its bounding box, so column
// reads array cells instead of map entries.
type denseCore struct {
	x0, z0, w, h int32
	in           []bool
}

func newDenseCore(core map[domain.Cell]bool) denseCore {
	x0, z0, x1, z1 := int32(math.MaxInt32), int32(math.MaxInt32), int32(math.MinInt32), int32(math.MinInt32)
	for c, ok := range core {
		if ok {
			x0, z0, x1, z1 = min(x0, c.X), min(z0, c.Z), max(x1, c.X), max(z1, c.Z)
		}
	}
	if x1 < x0 {
		return denseCore{}
	}
	d := denseCore{x0: x0, z0: z0, w: x1 - x0 + 1, h: z1 - z0 + 1}
	d.in = make([]bool, int(d.w)*int(d.h))
	for c, ok := range core {
		if ok {
			d.in[int(c.Z-z0)*int(d.w)+int(c.X-x0)] = true
		}
	}
	return d
}

// column is buildGround.column: the cells (x, z-coreHalf..z+coreHalf) all core.
func (d denseCore) column(x, z int32) bool {
	if x < d.x0 || x >= d.x0+d.w || z-coreHalf < d.z0 || z+coreHalf >= d.z0+d.h {
		return false
	}
	i := int(z-coreHalf-d.z0)*int(d.w) + int(x-d.x0)
	for dz := int32(0); dz <= 2*coreHalf; dz++ {
		if !d.in[i+int(dz)*int(d.w)] {
			return false
		}
	}
	return true
}

// siteGridStep is the coarse grid's spacing in cells.
const siteGridStep = 2

// siteEdgeGap is how far outside a rich patch an edge seed may sit.
const siteEdgeGap = 3

func (g buildGround) nearRich(c domain.Cell) bool {
	for _, d := range [][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
		for k := int32(1); k <= siteEdgeGap; k++ {
			if g.soil[domain.Cell{X: c.X + d[0]*k, Z: c.Z + d[1]*k}] == soilCostRich {
				return true
			}
		}
	}
	return false
}

func (g buildGround) besideRock(c domain.Cell) bool {
	for _, d := range [][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
		if g.rock[domain.Cell{X: c.X + d[0], Z: c.Z + d[1]}] {
			return true
		}
	}
	return false
}

func sortCells(cs []domain.Cell) {
	sort.Slice(cs, func(i, j int) bool {
		return cs[i].Z < cs[j].Z || cs[i].Z == cs[j].Z && cs[i].X < cs[j].X
	})
}

// stepped takes k cells evenly spaced through cs.
func stepped(cs []domain.Cell, k int) []domain.Cell {
	if len(cs) <= k {
		return cs
	}
	out := make([]domain.Cell, 0, k)
	for i := range k {
		out = append(out, cs[i*len(cs)/k])
	}
	return out
}

// siteGround is the map's size, its impassable cells and every cell's
// walking distance from the nearest open edge cell, shared by every
// candidate's wall and edge score.
type siteGround struct {
	w, h    int32
	blocked []bool
	walk    []int32 // steps from an open edge cell; -1 when unreachable
}

func newSiteGround(s MapSurvey) siteGround {
	w, h := s.Bounds.Width, s.Bounds.Height
	b := make([]bool, max(w*h, 0))
	for _, c := range s.Cells {
		if c.Cell.X >= 0 && c.Cell.X < w && c.Cell.Z >= 0 && c.Cell.Z < h && (c.Rock || c.Hazard || !c.Walkable && !c.Built) {
			b[c.Cell.Z*w+c.Cell.X] = true
		}
	}
	g := siteGround{w: w, h: h, blocked: b}
	g.walk = g.edgeWalk(nil)
	return g
}

// edgeWalk is one multi-source BFS over unblocked cells (8-connected, as a
// pawn walks) from every open cell on the map's outermost ring: raiders
// spawn only on open edge cells. dug are blocked cells a plan digs out, which
// then walk like open ground: a room in a mountain is as far from the edge as
// the walk to its entrance.
func (g siteGround) edgeWalk(dug map[domain.Cell]bool) []int32 {
	walk := make([]int32, len(g.blocked))
	var queue []int32
	for i := range walk {
		walk[i] = -1
		x, z := int32(i)%g.w, int32(i)/g.w
		if !g.blocked[i] && (x == 0 || z == 0 || x == g.w-1 || z == g.h-1) {
			walk[i] = 0
			queue = append(queue, int32(i))
		}
	}
	for head := 0; head < len(queue); head++ {
		i := queue[head]
		x, z := i%g.w, i/g.w
		for dz := int32(-1); dz <= 1; dz++ {
			for dx := int32(-1); dx <= 1; dx++ {
				nx, nz := x+dx, z+dz
				if nx < 0 || nz < 0 || nx >= g.w || nz >= g.h {
					continue
				}
				n := nz*g.w + nx
				if walk[n] >= 0 || g.blocked[n] && !dug[domain.Cell{X: nx, Z: nz}] {
					continue
				}
				walk[n] = walk[i] + 1
				queue = append(queue, n)
			}
		}
	}
	return walk
}

// walkDist is c's walking distance from the nearest open edge cell, or -1
// when c is off the map or no raider can reach it.
func (g siteGround) walkDist(c domain.Cell) int {
	if c.X < 0 || c.X >= g.w || c.Z < 0 || c.Z >= g.h {
		return -1
	}
	return int(g.walk[c.Z*g.w+c.X])
}

// eachIndex runs f for 0..n-1 across GOMAXPROCS workers, worker w taking
// the indexes w, w+W, w+2W... Rule 2 allows this because it is deterministic:
// each f writes only its own slot, so the result never depends on scheduling
// or on how many workers run.
func eachIndex(n int, f func(i int)) {
	workers := min(runtime.GOMAXPROCS(0), n)
	if workers <= 1 {
		for i := range n {
			f(i)
		}
		return
	}
	var wg sync.WaitGroup
	for w := range workers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := w; i < n; i += workers {
				f(i)
			}
		}()
	}
	wg.Wait()
}
