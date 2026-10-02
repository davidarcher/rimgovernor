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
// #1280 baseline fixture one Grow takes ~40-50 ms, so 96 seeds over the
// worker pool stay well inside siteBudget.
const siteCandidates = 96

// siteBudget is the wall time the whole fresh-siting pass should stay
// under on the #1280 fixture (BenchmarkLayoutSiteCore checks it).
const siteBudget = 2 * time.Second

// Score weights. Soil near the core is a gain, soil under rooms and
// hallways a loss (by soilCost), rock under them a dig cost, and a base
// room that did not fit outweighs any soil.
const (
	siteReach       = 20 // cells around the rooms counted as walking range
	siteNearWeight  = 1
	siteUnderWeight = 3
	siteRockWeight  = 1
	siteMissingRoom = 100000
)

// siteScore is one candidate's result.
type siteScore struct {
	seed  domain.Cell
	score int
	plan  LayoutPlan
}

// SiteCore grows plan as Grow does, for pawns colonists and tombs tomb
// rooms. A plan with no spine yet is sited: grown from each candidate
// seed over s, keeping the best-scoring result.
func SiteCore(plan LayoutPlan, s MapSurvey, pawns, tombs int, tier BuildTier) LayoutPlan {
	if len(plan.Spine) > 0 {
		return Grow(plan, pawns, tombs, tier)
	}
	g := newCoreGrid(plan.Zones, plan.Reservations).withSoil(s)
	if len(g.core) == 0 {
		return plan
	}
	seeds := g.siteSeeds(siteCandidates)
	if len(seeds) == 0 {
		return plan
	}
	scores := make([]siteScore, len(seeds))
	eachParallel(len(seeds), func(i int) {
		p := plan
		p.Spine = []SpineSegment{{From: seeds[i], To: seeds[i]}}
		p = Grow(p, pawns, tombs, tier)
		scores[i] = siteScore{seed: seeds[i], score: g.scoreSite(p) - siteEdgeCost(p, s.Bounds), plan: p}
	})
	rankSites(scores)
	// The wall terms (#1288) need PlanPerimeter, far dearer than Grow, so
	// only the best siteWallCandidates by core score are walled and reranked.
	walled := scores[:min(siteWallCandidates, len(scores))]
	ground := newSiteGround(s)
	eachParallel(len(walled), func(i int) {
		walled[i].score += g.scoreWall(PlanPerimeter(walled[i].plan, s), ground)
	})
	rankSites(walled)
	var top []string
	for _, sc := range scores[:min(3, len(scores))] {
		top = append(top, fmt.Sprintf("(%d,%d)=%d", sc.seed.X, sc.seed.Z, sc.score))
	}
	slog.Info("[layout] site scores top 3: "+strings.Join(top, " "), "candidates", len(seeds))
	return scores[0].plan
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
const siteGridStep = 8

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

// scoreSite scores a grown plan: soil within siteReach of its rooms and
// not under them counts for it; soilCost and rock under rooms and
// hallways, and every base room left out, count against it.
func (g coreGrid) scoreSite(p LayoutPlan) int {
	rooms := p.AllRooms()
	if len(rooms) == 0 {
		return -siteMissingRoom * (len(coreBaseRooms) + 1)
	}
	under := map[domain.Cell]bool{}
	box := rooms[0].Interior
	for _, r := range rooms {
		for _, c := range rectCells(r.Interior) {
			under[c] = true
		}
		box = union(box, r.Interior)
	}
	for _, h := range p.Hallways() {
		for _, c := range rectCells(pad(rectOf(h.From, h.To), SpineWidth/2)) {
			under[c] = true
		}
	}
	score := 0
	for c := range under {
		score -= siteUnderWeight * g.soil[c]
		if g.rock[c] {
			score -= siteRockWeight
		}
	}
	for _, c := range rectCells(pad(box, siteReach)) {
		if !under[c] {
			score += siteNearWeight * g.soil[c]
		}
	}
	have := map[ModuleRole]bool{}
	for _, r := range rooms {
		have[r.Role] = true
	}
	for _, role := range coreBaseRooms {
		if !have[role] {
			score -= siteMissingRoom
		}
	}
	return score
}

// siteEdgeClear is how far from the map edge a room stands before the
// edge stops costing it: raiders arrive at the edge, so a core pressed
// against it has no ground to meet them on, and barren ground near a
// mountain edge otherwise beats central soil on soil cost alone.
const (
	siteEdgeClear  = 50
	siteEdgeWeight = 3
)

// siteEdgeCost charges every room cell siteEdgeWeight per cell it stands
// closer to the map edge than siteEdgeClear, or a fifth of the map's
// short side on a small map.
func siteEdgeCost(p LayoutPlan, b Bounds) int {
	clear := min(siteEdgeClear, int(min(b.Width, b.Height))/5)
	cost := 0
	for _, r := range p.AllRooms() {
		for _, c := range rectCells(r.Interior) {
			d := min(c.X, c.Z, b.Width-1-c.X, b.Height-1-c.Z)
			cost += siteEdgeWeight * max(0, clear-int(d))
		}
	}
	return cost
}

func union(a, b Rectangle) Rectangle {
	x0, z0 := min(a.X, b.X), min(a.Z, b.Z)
	x1, z1 := max(a.X+a.Width, b.X+b.Width), max(a.Z+a.Height, b.Z+b.Height)
	return Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}
}

// siteWallCandidates is K, how many of the best sites by core score are
// walled with PlanPerimeter and rescored (#1288). One PlanPerimeter call
// on the #1280 fixture costs ~250-300 ms and ~250 MB; walling 8 in
// parallel took the whole pass from ~0.5 s to ~0.86 s (32 threads,
// quiet box), inside siteBudget.
const siteWallCandidates = 8

// Wall score weights (#1288): each wall cell is a build and defense cost,
// soil inside the wall (rich weighted by soilCost) a gain, and soil left
// outside it but within siteReach of the enclosure a loss.
const (
	siteWallWeight     = 2
	siteEnclosedWeight = 2
	siteOutsideWeight  = 1
)

// wallBarrier are the perimeter reservations a raider cannot walk
// through; the flood that finds the enclosure stops at them.
var wallBarrier = map[ReservationKind]bool{ReservePerimeter: true, ReservePerimeterLight: true, ReserveBridge: true, ReservePerimeterGap: true, ReserveGate: true, ReserveKillbox: true}

// siteGround is the map's size and its impassable cells, shared by every
// candidate's wall score.
type siteGround struct {
	w, h    int32
	blocked []bool
}

func newSiteGround(s MapSurvey) siteGround {
	w, h := s.Bounds.Width, s.Bounds.Height
	b := make([]bool, max(w*h, 0))
	for _, c := range s.Cells {
		if c.Cell.X >= 0 && c.Cell.X < w && c.Cell.Z >= 0 && c.Cell.Z < h && (c.Rock || !c.Walkable && !c.Built) {
			b[c.Cell.Z*w+c.Cell.X] = true
		}
	}
	return siteGround{w: w, h: h, blocked: b}
}

// scoreWall scores p's wall (p as PlanPerimeter returns it): the wall's
// cells against it, the soil it encloses (off the rooms) for it, and the
// soil outside it within siteReach of what it encloses against it. The
// enclosure is every open cell a flood from the map edge cannot reach.
func (g coreGrid) scoreWall(p LayoutPlan, ground siteGround) int {
	w, h := ground.w, ground.h
	if w < 1 || h < 1 {
		return 0
	}
	idx := func(c domain.Cell) (int32, bool) {
		if c.X < 0 || c.X >= w || c.Z < 0 || c.Z >= h {
			return 0, false
		}
		return c.Z*w + c.X, true
	}
	wall := make([]bool, w*h)
	walls := 0
	for _, r := range p.Reservations {
		if !wallBarrier[r.Kind] {
			continue
		}
		built := r.Kind == ReservePerimeter || r.Kind == ReservePerimeterLight || r.Kind == ReserveBridge
		for _, c := range rectCells(r.Area) {
			if i, ok := idx(c); ok && !wall[i] {
				wall[i] = true
				if built {
					walls++
				}
			}
		}
	}
	if walls == 0 {
		return 0
	}
	open := func(i int32) bool { return !wall[i] && !ground.blocked[i] }
	reached := make([]bool, w*h)
	var queue []int32
	push := func(x, z int32) {
		if i, ok := idx(domain.Cell{X: x, Z: z}); ok && open(i) && !reached[i] {
			reached[i] = true
			queue = append(queue, i)
		}
	}
	for x := range w {
		push(x, 0)
		push(x, h-1)
	}
	for z := range h {
		push(0, z)
		push(w-1, z)
	}
	for len(queue) > 0 {
		i := queue[len(queue)-1]
		queue = queue[:len(queue)-1]
		x, z := i%w, i/w
		push(x+1, z)
		push(x-1, z)
		push(x, z+1)
		push(x, z-1)
	}
	rooms := map[domain.Cell]bool{}
	for _, r := range p.AllRooms() {
		for _, c := range rectCells(r.Interior) {
			rooms[c] = true
		}
	}
	inside, outside := 0, 0
	var box Rectangle
	for i := range w * h {
		if !open(i) || reached[i] {
			continue
		}
		c := domain.Cell{X: i % w, Z: i / w}
		cell := Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}
		if box.Width == 0 {
			box = cell
		} else {
			box = union(box, cell)
		}
		if !rooms[c] {
			inside += g.soil[c]
		}
	}
	for _, c := range rectCells(pad(box, siteReach)) {
		if i, ok := idx(c); ok && reached[i] {
			outside += g.soil[c]
		}
	}
	return siteEnclosedWeight*inside - siteOutsideWeight*outside - siteWallWeight*walls
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
		if a.score != b.score {
			return a.score > b.score
		}
		return a.seed.Z < b.seed.Z || a.seed.Z == b.seed.Z && a.seed.X < b.seed.X
	})
}
