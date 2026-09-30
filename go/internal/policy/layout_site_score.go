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
	work := make(chan int)
	var wg sync.WaitGroup
	for range min(runtime.GOMAXPROCS(0), len(seeds)) {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := range work {
				p := plan
				p.Spine = []SpineSegment{{From: seeds[i], To: seeds[i]}}
				p = Grow(p, pawns, tombs, tier)
				scores[i] = siteScore{seed: seeds[i], score: g.scoreSite(p), plan: p}
			}
		}()
	}
	for i := range seeds {
		work <- i
	}
	close(work)
	wg.Wait()
	sort.SliceStable(scores, func(i, j int) bool {
		a, b := scores[i], scores[j]
		if a.score != b.score {
			return a.score > b.score
		}
		return a.seed.Z < b.seed.Z || a.seed.Z == b.seed.Z && a.seed.X < b.seed.X
	})
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

func union(a, b Rectangle) Rectangle {
	x0, z0 := min(a.X, b.X), min(a.Z, b.Z)
	x1, z1 := max(a.X+a.Width, b.X+b.Width), max(a.Z+a.Height, b.Z+b.Height)
	return Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}
}
