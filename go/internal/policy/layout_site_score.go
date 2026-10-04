package policy

import (
	"fmt"
	"log/slog"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Site scoring (#1285, epic #1279). A fresh plan no longer grows from the
// one centroid seed: SiteCore grows a full plan from each of a fixed,
// deterministic set of candidate seeds and keeps the best-scoring one.
// Replans extend the saved plan and never re-site.

// siteCandidates is N, the most seeds a fresh siting pass grows. On the
// #1280 baseline fixture one Grow takes ~40-50 ms; the map is paused while
// the first plan is sited, so ~780 seeds (the fixture yields that many) over the
// worker pool take ~4 s (32 threads), inside siteBudget.
const siteCandidates = 1000

// siteBudget is the wall time the whole fresh-siting pass should stay
// under on the #1280 fixture (BenchmarkLayoutSiteCore checks it).
const siteBudget = 10 * time.Second

// siteScore is one candidate's result, scored by Score's terms (#1952).
type siteScore struct {
	seed  domain.Cell
	score PlanScore
	plan  LayoutPlan
}

// SiteCore grows plan as Grow does, for pawns colonists and tombs tomb
// rooms. A plan with no spine yet is sited: grown from each candidate
// seed over s, keeping the best-scoring result.
func SiteCore(plan LayoutPlan, s MapSurvey, pawns, tombs int, tier BuildTier) LayoutPlan {
	if len(plan.Hallways()) > 0 {
		return growSoil(plan, surveySoil(s), pawns, tombs, tier)
	}
	g := newCoreGrid(plan.Zones, plan.Reservations).withSoil(s)
	if len(g.core) == 0 {
		return plan
	}
	scorer := planScorer{g: g, s: s, ground: newSiteGround(s)}
	var scores []siteScore
	var used obstacleLevel
	// Each obstacle level sites every seed over the ground left once its
	// obstacles are out; a level holds only if some plan places every base
	// room, routes and houses every colonist, else the next, looser level
	// runs, the last with no obstacle (rich soil at cost, #1921).
	for _, level := range obstacleLevels {
		scores, used = siteLevel(g, scorer, plan, level, pawns, tombs, tier), level
		if level == obstacleNone {
			break
		}
		var held []siteScore
		for _, sc := range scores {
			if sc.score.Passes() && !sc.plan.LayoutOutgrown(pawns) {
				held = append(held, sc)
			}
		}
		if len(held) > 0 {
			scores = held
			break
		}
	}
	if len(scores) == 0 {
		return plan
	}
	// The wall terms (#1288) need PlanPerimeter, far dearer than Grow, so
	// only the best siteWallCandidates by core score are walled and reranked.
	walled := scores[:min(siteWallCandidates, len(scores))]
	eachParallel(len(walled), func(i int) {
		walled[i].score = scorer.walled(walled[i].plan, walled[i].score)
	})
	rankSites(walled)
	var top []string
	for _, sc := range scores[:min(3, len(scores))] {
		top = append(top, fmt.Sprintf("(%d,%d)=%d", sc.seed.X, sc.seed.Z, sc.score.Total()))
	}
	slog.Info("[layout] site scores top 3: "+strings.Join(top, " "), "candidates", len(scores), "obstacles", used)
	return scores[0].plan
}

// siteLevel generates and scores a plan from each seed of the ground g
// leaves at level, best first. A level whose obstacles leave no seed
// returns none.
func siteLevel(g coreGrid, scorer planScorer, plan LayoutPlan, level obstacleLevel, pawns, tombs int, tier BuildTier) []siteScore {
	lg := g.withObstacles(g.coreObstacles(plan.Zones, level))
	if len(lg.core) == 0 {
		return nil
	}
	seeds := lg.siteSeeds(siteCandidates)
	scores := make([]siteScore, len(seeds))
	eachParallel(len(seeds), func(i int) {
		p := lg.generate(plan, seeds[i], pawns, tombs, tier)
		scores[i] = siteScore{seed: seeds[i], score: scorer.core(p), plan: p}
	})
	rankSites(scores)
	return scores
}

// siteSeeds is the candidate set, at most n seeds, every one passing
// column: the centroid seed, then an even share each of cells just
// outside a rich patch, cells along the rock edge and a coarse grid over
// the core ground, each stepped evenly through its (Z, X) order.
func (g coreGrid) siteSeeds(n int) []domain.Cell {
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
	for c := range g.core {
		if !g.column(c.X, c.Z) {
			continue
		}
		if g.soil[c] != soilCostRich && g.nearRich(c) {
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

// siteGridStep is the coarse grid's spacing in cells.
const siteGridStep = 2

// siteEdgeGap is how far outside a rich patch an edge seed may sit.
const siteEdgeGap = 3

func (g coreGrid) nearRich(c domain.Cell) bool {
	for _, d := range [][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
		for k := int32(1); k <= siteEdgeGap; k++ {
			if g.soil[domain.Cell{X: c.X + d[0]*k, Z: c.Z + d[1]*k}] == soilCostRich {
				return true
			}
		}
	}
	return false
}

func (g coreGrid) besideRock(c domain.Cell) bool {
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

// siteWallCandidates is K, how many of the best sites by core score are
// walled with PlanPerimeter and rescored (#1288). One PlanPerimeter call
// on the #1280 fixture costs ~250-300 ms and ~250 MB; walling 8 in
// parallel took the whole pass from ~0.5 s to ~0.86 s at 96 seeds (32 threads,
// quiet box), inside siteBudget.
const siteWallCandidates = 16

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
	g.walk = g.edgeWalk()
	return g
}

// edgeWalk is one multi-source BFS over unblocked cells (8-connected, as a
// pawn walks) from every open cell on the map's outermost ring: raiders
// spawn only on open edge cells.
func (g siteGround) edgeWalk() []int32 {
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
				if g.blocked[n] || walk[n] >= 0 {
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

// eachParallel runs f for 0..n-1 over the worker pool.
func eachParallel(n int, f func(i int)) {
	work := make(chan int)
	var wg sync.WaitGroup
	for range min(runtime.GOMAXPROCS(0), n) {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := range work {
				f(i)
			}
		}()
	}
	for i := range n {
		work <- i
	}
	close(work)
	wg.Wait()
}

// rankSites orders scores best first, ties by seed (Z, X).
func rankSites(scores []siteScore) {
	sort.SliceStable(scores, func(i, j int) bool {
		a, b := scores[i], scores[j]
		if a.score.Better(b.score) != b.score.Better(a.score) {
			return a.score.Better(b.score)
		}
		return a.seed.Z < b.seed.Z || a.seed.Z == b.seed.Z && a.seed.X < b.seed.X
	})
}
