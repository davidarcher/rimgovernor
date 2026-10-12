package policy

import (
	"fmt"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Topology embedding places the room groups (layout_cluster.go) on the site
// as area-sized rectangular blocks, coarsely: no regions, doors or walls yet
// (tiling is the next stage). It reuses the siting machinery rather than a
// parallel one: SiteCore's obstacle levels and candidate seeds, the PlanScore
// terms per cell (soil and rock, edge, centre), planWeights for every weight
// and the seeded searchRand. No constant is new.
//
// Objective, lower is better, over blocks b and group links (i, j):
//
//	sum_b  cells(b) x [Soil x soilCost + Rock x rock + Centre] + padded(b) x Edge
//	sum_l  Walk x weight(l) x (gap(i, j) - 1)       gap 1 = sharing a wall
//	sum_l  BadRoutes                                 per required link not adjacent
//
// The link weight is the clustering's summed flow weight (movements a day), so
// the walking term is the same trip-weighted distance PlanScore.Walk prices,
// with the station graph's flows instead of the role trips. Every cell term is
// a prefix sum over the map, so pricing a block is O(1) however big it is.
//
// Heuristic. The graph is built like Prim's tree: the heaviest group first at
// the nearest fit to a siting seed, then always the group with the most weight
// to what is placed (required links first), put at the best fitting position
// on the smallest ring around its placed neighbours that has one. That gives a
// compact, flow-ordered start. A seeded local search then lifts one block at
// a time and puts it back beside a linked block, beside any block, or at a
// random core cell, or swaps two blocks, keeping only strict gains. Why it
// should not plateau: the start is a different tree for each of siteSearchTop
// siting seeds (the same seeds SiteCore draws, spread over rich edges, rock
// edges and a grid), the random-cell and swap moves leave any basin the
// ring moves cannot, and the budget is siteSearchIters moves per block, so a
// big colony searches in proportion. The best start wins, so the result never
// scores below the greedy start of its best seed.
//
// Environment requirements drive placement only where the survey says
// something: open sky is a constraint (no rock or mountain roof under the
// block), and every block gets walls (enclosed). Cold is not a site
// requirement: a cold group carries its cooling station (ClusterStations), so
// mountain is only the rock dig cost every group pays; its thermal benefit and
// exposure to cold outside air are the climate envelope (#2880). A clean room
// has no site signal and is reported unsatisfied with a reason, not guessed at.
//
// Obstacle levels work as in SiteCore: the strictest level that places every
// group is kept, and the last level (soil priced, not banned) runs only when
// no stricter one places them all.

// Verdicts and reasons of embedding decisions.
const (
	EmbedPlace       = "place"
	EmbedUnplaceable = "unplaceable"
	EmbedUnsatisfied = "unsatisfied"

	ReasonEmbedPlaced          = "embed_placed"
	ReasonEmbedNoGround        = "embed_no_ground"
	ReasonEmbedOpenSkyGround   = "embed_open_sky_ground"
	ReasonEmbedNoSiteSignal    = "embed_no_site_signal"
	ReasonEmbedLinkNotAdjacent = "embed_link_not_adjacent"
)

// EmbedInput is everything the embedding reads.
type EmbedInput struct {
	Groups       []RoomGroup
	Zones        []LayoutZone
	Reservations []LayoutReservation
	Survey       MapSurvey
	// RoofDistance is the game's roof support distance (as for ClusterInput):
	// it bounds a block's depth.
	RoofDistance float64
}

// EmbeddedBlock is where a group's interior stands.
type EmbeddedBlock struct {
	Group    int
	Interior Rectangle
}

// EmbedDecision is one embedding decision for the caller to journal. Other is
// the second group of a link decision, -1 otherwise.
type EmbedDecision struct {
	Group, Other int
	Verdict      string
	Reason       string
	// Requirement names the environment of an unsatisfied requirement.
	Requirement EnvRequirement
}

// EmbedScore is the embedding's per-term values, signed like PlanScore (costs
// negative). Soil includes the rock dig cost; Walk is the weighted walking
// distance between linked blocks; UnmetLinks counts required links not
// adjacent, each charged planWeights.BadRoutes in Total.
type EmbedScore struct {
	Soil, Edge, Centre, Walk int
	UnmetLinks               int
	// Cells is the interior area placed.
	Cells int
}

// SiteTerms is the terms the old plan scores the same way: soil, edge, centre.
func (s EmbedScore) SiteTerms() int { return s.Soil + s.Edge + s.Centre }

// Total is the sum of the terms and the required-link charges.
func (s EmbedScore) Total() int {
	return s.Soil + s.Edge + s.Centre + s.Walk - planWeights.BadRoutes*s.UnmetLinks
}

// Embedding is the coarse placement.
type Embedding struct {
	// Level is the obstacle level the blocks were placed under; Seed the
	// siting seed whose start won.
	Level  obstacleLevel
	Seed   domain.Cell
	Blocks []EmbeddedBlock
	// Decisions has one entry per group (placed or unplaceable) and one per
	// unsatisfied requirement or required link.
	Decisions []EmbedDecision
	Score     EmbedScore
}

// Block is the group's block.
func (e Embedding) Block(group int) (Rectangle, bool) {
	for _, b := range e.Blocks {
		if b.Group == group {
			return b.Interior, true
		}
	}
	return Rectangle{}, false
}

// Unplaceable are the groups no position fit, with the reason.
func (e Embedding) Unplaceable() []EmbedDecision {
	var out []EmbedDecision
	for _, d := range e.Decisions {
		if d.Verdict == EmbedUnplaceable {
			out = append(out, d)
		}
	}
	return out
}

// prefix is a 2D prefix sum over the map.
type prefix struct {
	w, h int32
	s    []int64
}

func newPrefix(w, h int32, f func(c domain.Cell) int) prefix {
	p := prefix{w: w, h: h, s: make([]int64, int(w+1)*int(h+1))}
	for z := int32(0); z < h; z++ {
		for x := int32(0); x < w; x++ {
			v := int64(f(domain.Cell{X: x, Z: z}))
			p.s[int(z+1)*int(w+1)+int(x+1)] = v + p.s[int(z)*int(w+1)+int(x+1)] + p.s[int(z+1)*int(w+1)+int(x)] - p.s[int(z)*int(w+1)+int(x)]
		}
	}
	return p
}

// sum is the total over r, which lies inside the map.
func (p prefix) sum(r Rectangle) int64 {
	x0, z0, x1, z1 := int(r.X), int(r.Z), int(r.X+r.Width), int(r.Z+r.Height)
	stride := int(p.w + 1)
	return p.s[z1*stride+x1] - p.s[z0*stride+x1] - p.s[z1*stride+x0] + p.s[z0*stride+x0]
}

func (p prefix) inside(r Rectangle) bool {
	return r.X >= 0 && r.Z >= 0 && r.X+r.Width <= p.w && r.Z+r.Height <= p.h
}

// embedder is one level's immutable pricing of the site.
type embedder struct {
	groups []RoomGroup
	shapes [][][2]int32 // per group, the interiors (width, height) it may take
	sky    []bool       // per group: needs open sky

	level, base        prefix // core cells at the level / with no obstacle
	soil, edge, centre prefix // weighted cell costs
	solid              prefix // rock or mountain roof
	core               []domain.Cell
	seeds              []domain.Cell
}

// blockShapes are the interiors a group of area cells may take: as deep as
// the roof allows is never needed, so the squarest rectangle within depth,
// and its turn.
func blockShapes(area, depth int) [][2]int32 {
	area = max(area, 1)
	h := min(depth, int(math.Ceil(math.Sqrt(float64(area)))))
	w := (area + h - 1) / h
	out := [][2]int32{{int32(w), int32(h)}}
	if w != h {
		out = append(out, [2]int32{int32(h), int32(w)})
	}
	return out
}

// groupShapes are the interiors a group may take: a strip of its Width and the
// length its area needs, and its turn, else the squarest blocks.
func groupShapes(g RoomGroup, depth int) [][2]int32 {
	if g.Width <= 0 {
		return blockShapes(g.Area, depth)
	}
	length := int32((max(g.Area, 1) + int(g.Width) - 1) / int(g.Width))
	return [][2]int32{{length, g.Width}, {g.Width, length}}
}

func newEmbedder(in EmbedInput, depth int, scorer planScorer, level obstacleLevel) *embedder {
	w, h := in.Survey.Bounds.Width, in.Survey.Bounds.Height
	e := &embedder{groups: in.Groups}
	for _, g := range in.Groups {
		e.shapes = append(e.shapes, groupShapes(g, depth))
		e.sky = append(e.sky, slices.Contains(g.Requires, EnvOpenSky))
	}
	cells := map[domain.Cell]SurveyCell{}
	for _, c := range in.Survey.Cells {
		cells[c.Cell] = c
	}
	lg := scorer.g.withObstacles(scorer.g.coreObstacles(in.Zones, level))
	e.level = newPrefix(w, h, func(c domain.Cell) int { return b2i(lg.core[c]) })
	e.base = newPrefix(w, h, func(c domain.Cell) int { return b2i(scorer.g.core[c]) })
	e.soil = newPrefix(w, h, func(c domain.Cell) int {
		return -(planWeights.Soil*scorer.g.soil[c] + planWeights.Rock*b2i(scorer.g.rock[c]))
	})
	clear := scorer.edgeClear()
	e.edge = newPrefix(w, h, func(c domain.Cell) int { return -edgeCellCost(scorer.ground, c, clear) })
	e.centre = newPrefix(w, h, func(c domain.Cell) int { return -scorer.centreCell(c) })
	e.solid = newPrefix(w, h, func(c domain.Cell) int { return b2i(scorer.g.rock[c] || cells[c].ThickRoof) })
	for c := range lg.core {
		e.core = append(e.core, c)
	}
	sortCells(e.core)
	if len(e.core) == 0 {
		return e
	}
	e.seeds = lg.siteSeeds(siteSearchTop)
	if len(e.seeds) == 0 {
		e.seeds = stepped(e.core, siteSearchTop)
	}
	return e
}

func b2i(b bool) int {
	if b {
		return 1
	}
	return 0
}

// gapDist is the walking gap between two interiors in cells: 1 when they share
// a wall with overlapping sides (adjacent), the distance between them
// otherwise. Interiors collide when the walls of one would touch the other
// (collides).
func gapDist(a, b Rectangle) int {
	sx, sz := axisGap(a.X, a.Width, b.X, b.Width), axisGap(a.Z, a.Height, b.Z, b.Height)
	switch {
	case sx >= 1 && sz >= 1:
		return sx + sz
	case sx >= 1:
		return sx + b2i(sz == 0)
	case sz >= 1:
		return sz + b2i(sx == 0)
	}
	return 0
}

func axisGap(a0, al, b0, bl int32) int {
	return int(max(b0-(a0+al), a0-(b0+bl)))
}

// collides reports interiors that overlap or have no wall cell between them.
func collides(a, b Rectangle) bool {
	return axisGap(a.X, a.Width, b.X, b.Width) < 1 && axisGap(a.Z, a.Height, b.Z, b.Height) < 1
}

func (e *embedder) fits(i int, r Rectangle, blocks []Rectangle, sky bool) bool {
	ring := pad(r, 1)
	if !e.base.inside(ring) || e.base.sum(ring) != int64(ring.Width)*int64(ring.Height) ||
		e.level.sum(r) != int64(r.Width)*int64(r.Height) {
		return false
	}
	if sky && e.solid.sum(r) != 0 {
		return false
	}
	for j, b := range blocks {
		if j != i && b.Width > 0 && collides(r, b) {
			return false
		}
	}
	return true
}

// blockCost is the objective's share of group i at r: cell terms and every
// link to a placed block. Higher is worse.
func (e *embedder) blockCost(i int, r Rectangle, blocks []Rectangle) float64 {
	cost := float64(-(e.soil.sum(r) + e.centre.sum(r) + e.edge.sum(pad(r, 1))))
	for _, l := range e.groups[i].Links {
		if b := blocks[l.To]; b.Width > 0 && l.To != i {
			cost += linkCost(l, gapDist(r, b))
		}
	}
	return cost
}

func linkCost(l GroupLink, gap int) float64 {
	c := float64(planWeights.Walk) * l.Weight * float64(gap-1)
	if l.Required && gap != 1 {
		c += float64(planWeights.BadRoutes)
	}
	return c
}

// better ranks candidate positions: lower cost, then (Z, X).
func better(cost float64, r Rectangle, bestCost float64, best Rectangle, found bool) bool {
	if !found || cost < bestCost {
		return true
	}
	return cost == bestCost && (r.Z < best.Z || r.Z == best.Z && r.X < best.X)
}

// placeNear is the cheapest fitting position on the smallest ring around the
// anchors that has one.
func (e *embedder) placeNear(i int, blocks []Rectangle, anchors []int) (Rectangle, bool) {
	maxRing := int(e.level.w + e.level.h)
	for ring := 1; ring <= maxRing; ring++ {
		var best Rectangle
		var bestCost float64
		found := false
		for _, a := range anchors {
			ab := blocks[a]
			for _, sh := range e.shapes[i] {
				for z := ab.Z - sh[1] - int32(ring); z <= ab.Z+ab.Height+int32(ring); z++ {
					for x := ab.X - sh[0] - int32(ring); x <= ab.X+ab.Width+int32(ring); x++ {
						r := Rectangle{X: x, Z: z, Width: sh[0], Height: sh[1]}
						if gapDist(r, ab) != ring || !e.fits(i, r, blocks, e.sky[i]) {
							continue
						}
						if c := e.blockCost(i, r, blocks); better(c, r, bestCost, best, found) {
							best, bestCost, found = r, c, true
						}
					}
				}
			}
		}
		if found {
			return best, true
		}
	}
	return Rectangle{}, false
}

// placeAnywhere is the cheapest fitting position on the map, or the fitting
// one nearest toward when toward is set (a start's first block).
func (e *embedder) placeAnywhere(i int, blocks []Rectangle, sky bool, toward *domain.Cell) (Rectangle, bool) {
	var best Rectangle
	var bestCost float64
	found := false
	for _, c := range e.core {
		for _, sh := range e.shapes[i] {
			r := Rectangle{X: c.X, Z: c.Z, Width: sh[0], Height: sh[1]}
			if !e.fits(i, r, blocks, sky) {
				continue
			}
			var cost float64
			if toward != nil {
				dx, dz := float64(r.X+r.Width/2-toward.X), float64(r.Z+r.Height/2-toward.Z)
				cost = dx*dx + dz*dz
			} else {
				cost = e.blockCost(i, r, blocks)
			}
			if better(cost, r, bestCost, best, found) {
				best, bestCost, found = r, cost, true
			}
		}
	}
	return best, found
}

// start builds the greedy embedding from a siting seed and searches it.
func (e *embedder) start(seed domain.Cell, iters int) []Rectangle {
	n := len(e.groups)
	blocks := make([]Rectangle, n)
	done := make([]bool, n) // tried: placed or unplaceable
	total := func(i int) float64 {
		t := 0.0
		for _, l := range e.groups[i].Links {
			t += l.Weight
		}
		return t
	}
	for step := 0; step < n; step++ {
		pick, pickReq, pickW := -1, false, -1.0
		for i := range e.groups {
			if done[i] {
				continue
			}
			req, w := false, 0.0
			for _, l := range e.groups[i].Links {
				if blocks[l.To].Width > 0 {
					w += l.Weight
					req = req || l.Required
				}
			}
			if w == 0 {
				// Unlinked to what is placed: after every linked group (their
				// weights are positive), the heavier first.
				w = -1 / (1 + total(i))
			}
			if pick < 0 || req && !pickReq || req == pickReq && w > pickW {
				pick, pickReq, pickW = i, req, w
			}
		}
		done[pick] = true
		var anchors []int
		for _, l := range e.groups[pick].Links {
			if blocks[l.To].Width > 0 {
				anchors = append(anchors, l.To)
			}
		}
		var r Rectangle
		var ok bool
		if len(anchors) > 0 {
			r, ok = e.placeNear(pick, blocks, anchors)
		} else if step == 0 {
			r, ok = e.placeAnywhere(pick, blocks, e.sky[pick], &seed)
		} else {
			r, ok = e.placeAnywhere(pick, blocks, e.sky[pick], nil)
		}
		if ok {
			blocks[pick] = r
		}
	}
	e.search(blocks, seed, iters)
	return blocks
}

// search is the seeded local search: siteSearchIters moves per block, a move
// kept only when the objective strictly falls.
func (e *embedder) search(blocks []Rectangle, seed domain.Cell, iters int) {
	var placed []int
	for i, b := range blocks {
		if b.Width > 0 {
			placed = append(placed, i)
		}
	}
	if len(placed) == 0 {
		return
	}
	rng := newSearchRand(seed)
	for range iters * len(placed) {
		i := placed[rng.intn(len(placed))]
		old := blocks[i]
		switch rng.intn(4) {
		case 0, 1: // beside a linked block, else any block
			var anchors []int
			for _, l := range e.groups[i].Links {
				if blocks[l.To].Width > 0 {
					anchors = append(anchors, l.To)
				}
			}
			if len(anchors) == 0 || rng.intn(2) == 0 {
				anchors = placed
			}
			a := anchors[rng.intn(len(anchors))]
			if a == i {
				continue
			}
			before := e.blockCost(i, old, blocks)
			blocks[i] = Rectangle{}
			if r, ok := e.placeNear(i, blocks, []int{a}); ok && e.blockCost(i, r, blocks) < before {
				blocks[i] = r
			} else {
				blocks[i] = old
			}
		case 2: // a random core cell
			before := e.blockCost(i, old, blocks)
			blocks[i] = Rectangle{}
			c := e.core[rng.intn(len(e.core))]
			sh := e.shapes[i][rng.intn(len(e.shapes[i]))]
			r := Rectangle{X: c.X, Z: c.Z, Width: sh[0], Height: sh[1]}
			if e.fits(i, r, blocks, e.sky[i]) && e.blockCost(i, r, blocks) < before {
				blocks[i] = r
			} else {
				blocks[i] = old
			}
		default: // swap with another block
			j := placed[rng.intn(len(placed))]
			if j == i {
				continue
			}
			oldJ := blocks[j]
			before := e.blockCost(i, old, blocks) + e.blockCost(j, oldJ, blocks)
			blocks[i], blocks[j] = Rectangle{}, Rectangle{}
			ri, okI := e.placeAt(i, blocks, oldJ)
			if okI {
				blocks[i] = ri
			}
			rj, okJ := e.placeAt(j, blocks, old)
			if okI && okJ {
				blocks[j] = rj
				if e.blockCost(i, ri, blocks)+e.blockCost(j, rj, blocks) < before {
					continue
				}
			}
			blocks[i], blocks[j] = old, oldJ
		}
	}
}

// placeAt is group i's block with its top-left at at's, in the first shape that fits.
func (e *embedder) placeAt(i int, blocks []Rectangle, at Rectangle) (Rectangle, bool) {
	var best Rectangle
	var bestCost float64
	found := false
	for _, sh := range e.shapes[i] {
		r := Rectangle{X: at.X, Z: at.Z, Width: sh[0], Height: sh[1]}
		if !e.fits(i, r, blocks, e.sky[i]) {
			continue
		}
		if c := e.blockCost(i, r, blocks); better(c, r, bestCost, best, found) {
			best, bestCost, found = r, c, true
		}
	}
	return best, found
}

// objective is the whole embedding's cost.
func (e *embedder) objective(blocks []Rectangle) float64 {
	total := 0.0
	for i, r := range blocks {
		if r.Width == 0 {
			continue
		}
		total += e.blockCost(i, r, blocks)
		// blockCost counts each link from both ends; the pair is one cost.
		for _, l := range e.groups[i].Links {
			if b := blocks[l.To]; b.Width > 0 && l.To > i {
				total -= linkCost(l, gapDist(r, b))
			}
		}
	}
	return total
}

// Embed places the groups on the site. The result is deterministic: the seeds
// and the search stream come from the site, and ties break by position.
func Embed(in EmbedInput) (Embedding, error) {
	depth, _, err := roomLimits(in.RoofDistance)
	if err != nil {
		return Embedding{}, err
	}
	for i, g := range in.Groups {
		if g.ID != i {
			return Embedding{}, fmt.Errorf("%w: group %d has id %d", ErrCluster, i, g.ID)
		}
	}
	scorer := newPlanScorer(in.Zones, in.Reservations, in.Survey)
	var best *embedRun
	for _, level := range obstacleLevels {
		run := embedLevel(in, depth, scorer, level)
		if best == nil || run.unplaced < best.unplaced {
			best = run
		}
		if run.unplaced == 0 {
			break
		}
	}
	return best.embedding(in, scorer), nil
}

type embedRun struct {
	e        *embedder
	level    obstacleLevel
	seed     domain.Cell
	blocks   []Rectangle
	unplaced int
}

func embedLevel(in EmbedInput, depth int, scorer planScorer, level obstacleLevel) *embedRun {
	e := newEmbedder(in, depth, scorer, level)
	if len(in.Groups) == 0 || len(e.core) == 0 {
		return &embedRun{e: e, level: level, blocks: make([]Rectangle, len(in.Groups)), unplaced: len(in.Groups)}
	}
	runs := make([]*embedRun, len(e.seeds))
	eachIndex(len(runs), func(i int) {
		blocks := e.start(e.seeds[i], siteSearchIters)
		run := &embedRun{e: e, level: level, seed: e.seeds[i], blocks: blocks}
		for _, b := range blocks {
			if b.Width == 0 {
				run.unplaced++
			}
		}
		runs[i] = run
	})
	best, bestObj := runs[0], runs[0].e.objective(runs[0].blocks)
	for _, r := range runs[1:] {
		if obj := e.objective(r.blocks); r.unplaced < best.unplaced || r.unplaced == best.unplaced && obj < bestObj {
			best, bestObj = r, obj
		}
	}
	return best
}

// embedding reads the run into the result: the blocks, the score and a
// decision for every group and every requirement the site cannot meet.
func (r *embedRun) embedding(in EmbedInput, scorer planScorer) Embedding {
	e := r.e
	out := Embedding{Level: r.level, Seed: r.seed}
	for i, g := range in.Groups {
		b := r.blocks[i]
		if b.Width == 0 {
			reason := ReasonEmbedNoGround
			if e.sky[i] {
				if _, ok := e.placeAnywhere(i, r.blocks, false, nil); ok {
					reason = ReasonEmbedOpenSkyGround
				}
			}
			out.Decisions = append(out.Decisions, EmbedDecision{Group: i, Other: -1, Verdict: EmbedUnplaceable, Reason: reason})
			continue
		}
		out.Blocks = append(out.Blocks, EmbeddedBlock{Group: i, Interior: b})
		out.Decisions = append(out.Decisions, EmbedDecision{Group: i, Other: -1, Verdict: EmbedPlace, Reason: ReasonEmbedPlaced})
		out.Score.Cells += int(b.Width * b.Height)
		out.Score.Soil += int(e.soil.sum(b))
		out.Score.Centre += int(e.centre.sum(b))
		out.Score.Edge += int(e.edge.sum(pad(b, 1)))
		for _, req := range g.Requires {
			unsat := func(reason string) {
				out.Decisions = append(out.Decisions, EmbedDecision{Group: i, Other: -1, Verdict: EmbedUnsatisfied, Reason: reason, Requirement: req})
			}
			switch req {
			case EnvClean:
				unsat(ReasonEmbedNoSiteSignal)
			}
		}
		for _, l := range g.Links {
			ob := r.blocks[l.To]
			if ob.Width == 0 || l.To < i {
				continue
			}
			gap := gapDist(b, ob)
			out.Score.Walk -= int(math.Round(float64(planWeights.Walk) * l.Weight * float64(gap-1)))
			if l.Required && gap != 1 {
				out.Score.UnmetLinks++
				out.Decisions = append(out.Decisions, EmbedDecision{Group: i, Other: l.To, Verdict: EmbedUnsatisfied, Reason: ReasonEmbedLinkNotAdjacent})
			}
		}
	}
	return out
}

// ObstacleLevel is the obstacle level the blocks were placed under: 0 is the
// strictest (every field cell and rich soil avoided), higher is looser.
func (e Embedding) ObstacleLevel() int { return int(e.Level) }
