package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The perimeter as a routing cut (#2973). The wall is about routing, not a
// shape: the cheapest set of wall cells such that every walk from a map edge
// to the base crosses a wall, or passes the killbox opening. Terrain that a
// pawn cannot cross (rock, lava, deep water) is a free barrier, so the cut uses
// it first; only walls count as barriers (not sandbags, not traps).
//
// Ordinary raiders path ByPawn, where a wall cell is impassable
// (PathFinderJob prices it 10000): they never breach while any route exists
// and walk to the opening however long the detour, so one unbroken cell of
// wall already funnels them. A diagonal chain of wall cells is sealed, since
// PathFinderJob refuses a diagonal step past a blocked orthogonal neighbour,
// so the cut is found on the four-connected walkable grid.
//
// Sappers (JobGiver_AISapper, PassAllDestroyableThings) price a destroyable
// wall cell at Cost_BlockedWallBase (70) plus 0.2 per hit point, on top of the
// step, and so weigh breaching against the detour to the opening; the cost of
// breaching is linear in the wall's thickness. The thickness of each stretch
// of the cut is the least that leaves the detour the cheaper way
// (SapperThickness). Breachers (BreachingGrid) price a wall at 20 + 0.015 per
// hit point, barely moved by thickness: a wall is not the defence against
// them, the killbox and turrets are. The raid generator rolls the sapper
// strategy (ImmediateAttackSappers, selection weight 0 below 700 raid points,
// 0.4 at 1000) for the factions with a canBeSapper pawn kind (pirates,
// mercenaries, tribes, the Empire, wasters), so early raids walk and later ones
// may tunnel; the thickness is designed for the later ones.
//
// The cut is advice beside the ring PlanPerimeter traces: the runtime only
// journals it (logPerimeterCut).

// PerimeterTable is the cut's one judgment table, registered in DefTables so a
// renamed def fails the dangling-reference gate. WallDef is the building the
// cut is walled with. DetourBudgetTicks is the one judgment: the longest
// detour, in ticks of walking, the thickness is designed against. The sapper
// formula alone would thicken a stretch far from the opening without limit (a
// 250-cell map has detours of thousands of ticks); a stretch whose detour is
// longer than the budget is designed for the budget and left breachable past
// it, which the killbox and turrets answer. 1000 ticks is about 77 cells of
// walking, which makes the weakest wall (wood) four thick and granite six:
// roughly twice today's three-thick ring where the detour is longest.
type PerimeterTable struct {
	WallDef           string
	DetourBudgetTicks float64
}

var perimeterTable = PerimeterTable{WallDef: "Wall", DetourBudgetTicks: 1000}

// PerimeterDefs is the cut's judgment table.
func PerimeterDefs() PerimeterTable { return perimeterTable }

// PerimeterWall is the wall's strength: the MaxHitPoints of the wall the cut is
// built from on firm ground and of the wood wall laid on light footing and on
// a plain bridge. An unknown strength leaves the thickness at one cell.
type PerimeterWall struct {
	HP, LightHP domain.Fact[float64]
}

// SapperStepTicks is what a sapper pays to take one wall cell down and step
// into it: Cost_BlockedWallBase plus 0.2 per hit point, plus the step.
func SapperStepTicks(hp float64) float64 {
	return pathBlockedWallBase + pathBlockedWallPerHP*hp + pathMoveCardinal
}

// SapperThickness is the thickness of a stretch whose detour to the opening is
// detourTicks: the least N for which N*SapperStepTicks(hp) is at least the
// detour (capped at the table's budget), and at least one wall.
func SapperThickness(hp, detourTicks float64) int {
	if hp <= 0 {
		return 1
	}
	d := math.Min(detourTicks, perimeterTable.DetourBudgetTicks)
	return max(1, int(math.Ceil(d/SapperStepTicks(hp))))
}

// The cut's reasons; the caller journals them as layout_plan rows with target
// perimeter_cut (PerimeterCutDecisions).
const (
	// ReasonCutTraced: the cut closes every route but the opening.
	ReasonCutTraced = "cut_traced"
	// ReasonCutOpen: the walls cannot close a route (ground nothing can wall,
	// or the opening's lane touches the base), so the cut leaves it.
	ReasonCutOpen = "cut_unclosable"
	// ReasonThicknessDetour: the thickness follows the stretch's detour to the
	// opening.
	ReasonThicknessDetour = "thickness_sapper_detour"
	// ReasonThicknessCapped: the detour is past the budget; the thickness is
	// designed for the budget.
	ReasonThicknessCapped = "thickness_capped_at_budget"
	// ReasonThicknessNoDetour: no walk along the outside reaches the opening,
	// so no raider arrives there (an entry cell must reach the colony), and
	// one wall is enough.
	ReasonThicknessNoDetour = "thickness_no_route_to_opening"
	// ReasonThicknessUnknown: the wall's hit points are unknown.
	ReasonThicknessUnknown = "thickness_hp_unknown"
)

// CutSegment is a run of the cut's cells that share one thickness.
type CutSegment struct {
	Cells       []domain.Cell
	Thickness   int
	DetourTicks float64
	HP          float64
	Reason      string
}

// PerimeterCutDecision is one decision for the caller to journal.
type PerimeterCutDecision struct {
	Verdict, Reason string
	Attrs           map[string]any
}

// PerimeterCut is the advised wall: Cut is the one-cell chain that closes the
// routes, Walls the cut thickened outward by its segments, Unclosable the cut
// cells that stand on ground nothing can wall.
type PerimeterCut struct {
	Cut, Walls, Unclosable []domain.Cell
	Segments               []CutSegment
	Decisions              []PerimeterCutDecision
}

// cutGap is the cost of a cell nothing can wall: priced so the cut crosses it
// only when no other cut exists, never a hard veto.
const cutGap = 1000

// cutGrid is the grid the cut is found on: a barrier is free, impassable
// terrain; cost is the price of walling a cell (0 where the cut may not stand);
// source cells are where raiders appear and sink cells are the base.
type cutGrid struct {
	w, h         int32
	barrier      []bool
	cost         []int32
	source, sink []bool
}

func newCutGrid(w, h int32) cutGrid {
	n := w * h
	return cutGrid{w: w, h: h, barrier: make([]bool, n), cost: make([]int32, n), source: make([]bool, n), sink: make([]bool, n)}
}

func (g cutGrid) idx(c domain.Cell) (int32, bool) {
	if c.X < 0 || c.Z < 0 || c.X >= g.w || c.Z >= g.h {
		return 0, false
	}
	return c.Z*g.w + c.X, true
}

func (g cutGrid) cell(i int32) domain.Cell { return domain.Cell{X: i % g.w, Z: i / g.w} }

// neighbours4 calls f on the in-map orthogonal neighbours of i.
func (g cutGrid) neighbours4(i int32, f func(j int32)) {
	x, z := i%g.w, i/g.w
	if z > 0 {
		f(i - g.w)
	}
	if x > 0 {
		f(i - 1)
	}
	if x < g.w-1 {
		f(i + 1)
	}
	if z < g.h-1 {
		f(i + g.w)
	}
}

// flowNet is a flow network in arc-pair form (arc e and e^1 are each other's
// reverse), solved by Dinic's algorithm. Arcs are added in a fixed order, so the
// flow and the cut it yields are deterministic.
type flowNet struct {
	head, level, it []int32
	next, to, cap   []int32
}

const flowInf int32 = 1 << 30

func newFlowNet(nodes int32) *flowNet {
	f := &flowNet{head: make([]int32, nodes), level: make([]int32, nodes), it: make([]int32, nodes)}
	for i := range f.head {
		f.head[i] = -1
	}
	return f
}

func (f *flowNet) arc(u, v, c int32) {
	f.next, f.to, f.cap = append(f.next, f.head[u]), append(f.to, v), append(f.cap, c)
	f.head[u] = int32(len(f.to) - 1)
	f.next, f.to, f.cap = append(f.next, f.head[v]), append(f.to, u), append(f.cap, 0)
	f.head[v] = int32(len(f.to) - 1)
}

func (f *flowNet) bfs(s, t int32) bool {
	for i := range f.level {
		f.level[i] = -1
	}
	f.level[s] = 0
	queue := []int32{s}
	for i := 0; i < len(queue); i++ {
		u := queue[i]
		for e := f.head[u]; e >= 0; e = f.next[e] {
			if v := f.to[e]; f.cap[e] > 0 && f.level[v] < 0 {
				f.level[v] = f.level[u] + 1
				queue = append(queue, v)
			}
		}
	}
	return f.level[t] >= 0
}

func (f *flowNet) dfs(u, t, limit int32) int32 {
	if u == t {
		return limit
	}
	for ; f.it[u] >= 0; f.it[u] = f.next[f.it[u]] {
		e := f.it[u]
		v := f.to[e]
		if f.cap[e] <= 0 || f.level[v] != f.level[u]+1 {
			continue
		}
		if pushed := f.dfs(v, t, min(limit, f.cap[e])); pushed > 0 {
			f.cap[e] -= pushed
			f.cap[e^1] += pushed
			return pushed
		}
	}
	return 0
}

func (f *flowNet) maxFlow(s, t int32) int64 {
	var flow int64
	for f.bfs(s, t) {
		copy(f.it, f.head)
		for {
			pushed := f.dfs(s, t, flowInf)
			if pushed == 0 {
				break
			}
			flow += int64(pushed)
		}
	}
	return flow
}

// reaching is the nodes that can still reach t in the residual network.
func (f *flowNet) reaching(t int32) []bool {
	seen := make([]bool, len(f.head))
	seen[t] = true
	queue := []int32{t}
	for i := 0; i < len(queue); i++ {
		v := queue[i]
		for e := f.head[v]; e >= 0; e = f.next[e] {
			if u := f.to[e]; !seen[u] && f.cap[e^1] > 0 {
				seen[u] = true
				queue = append(queue, u)
			}
		}
	}
	return seen
}

// routingCut finds the cheapest set of wall cells separating the sources from
// the sink on the four-connected passable grid: a minimum vertex cut, by
// maximum flow with each cell split into an in and an out node whose arc costs
// the wall. Of the cuts of least cost it takes the one nearest the sink, so
// the wall hugs the base rather than the map edge. flow is the cut's cost; a
// flow at flowInf means no cut exists (a source touches the sink).
func routingCut(g cutGrid) (cut []int32, flow int64) {
	n := g.w * g.h
	s, t := 2*n, 2*n+1
	net := newFlowNet(2*n + 2)
	for i := int32(0); i < n; i++ {
		if g.barrier[i] {
			continue
		}
		c := g.cost[i]
		if c == 0 {
			c = flowInf
		}
		net.arc(2*i, 2*i+1, c)
		if g.source[i] {
			net.arc(s, 2*i, flowInf)
		}
		if g.sink[i] {
			net.arc(2*i+1, t, flowInf)
		}
		g.neighbours4(i, func(j int32) {
			if !g.barrier[j] {
				net.arc(2*i+1, 2*j, flowInf)
			}
		})
	}
	flow = net.maxFlow(s, t)
	if flow >= int64(flowInf) {
		return nil, flow
	}
	toSink := net.reaching(t)
	for i := int32(0); i < n; i++ {
		if !g.barrier[i] && g.cost[i] > 0 && !toSink[2*i] && toSink[2*i+1] {
			cut = append(cut, i)
		}
	}
	return cut, flow
}

// cutInput is a cut problem with its thickness inputs: hp is the wall's hit
// points per cell (0 unknown), mouth the opening's outer cell the detour is
// walked to, and keepOffWalls the cells thickening never walls (the opening's
// channel and lane).
type cutInput struct {
	grid         cutGrid
	hp           []float64
	mouth        int32
	hasOpening   bool
	hardToWall   []bool // cells nothing can wall (priced cutGap)
	keepOffWalls []bool // cells no thickening may wall: the opening's channel and lane
}

// designCut finds the routing cut and its thickness.
func designCut(in cutInput) PerimeterCut {
	g := in.grid
	cut, flow := routingCut(g)
	var out PerimeterCut
	if flow >= int64(flowInf) {
		out.Decisions = append(out.Decisions, PerimeterCutDecision{Verdict: EmbedUnplaceable, Reason: ReasonCutOpen, Attrs: map[string]any{"why": "source_touches_base"}})
		return out
	}
	inCut := make([]bool, len(g.barrier))
	for _, i := range cut {
		inCut[i] = true
		if in.hardToWall != nil && in.hardToWall[i] {
			out.Unclosable = append(out.Unclosable, g.cell(i))
		}
	}

	// Outside is what the sources reach with the cut standing.
	outside := make([]bool, len(g.barrier))
	var queue []int32
	for i := range g.source {
		if g.source[i] && !g.barrier[i] && !inCut[i] {
			outside[i] = true
			queue = append(queue, int32(i))
		}
	}
	for q := 0; q < len(queue); q++ {
		g.neighbours4(queue[q], func(j int32) {
			if !outside[j] && !g.barrier[j] && !inCut[j] && !g.sink[j] {
				outside[j] = true
				queue = append(queue, j)
			}
		})
	}

	// The walk from the opening's mouth along the outside, in cells.
	walk := make([]int32, len(g.barrier))
	for i := range walk {
		walk[i] = -1
	}
	if in.hasOpening {
		walk[in.mouth] = 0
		queue = append(queue[:0], in.mouth)
		for q := 0; q < len(queue); q++ {
			u := queue[q]
			g.neighbours4(u, func(j int32) {
				if walk[j] < 0 && !g.barrier[j] && !inCut[j] && !g.sink[j] && outside[j] {
					walk[j] = walk[u] + 1
					queue = append(queue, j)
				}
			})
		}
	}

	// Each cut cell's thickness from the detour of a raider that meets it.
	type cellDesign struct {
		n      int
		detour float64
		hp     float64
		reason string
	}
	design := map[int32]cellDesign{}
	for _, i := range cut {
		best := int32(-1)
		g.neighbours4(i, func(j int32) {
			if outside[j] && walk[j] >= 0 && (best < 0 || walk[j] < best) {
				best = walk[j]
			}
		})
		hp := in.hp[i]
		d := cellDesign{n: 1, hp: hp}
		switch {
		case hp <= 0:
			d.reason = ReasonThicknessUnknown
		case best < 0:
			d.reason = ReasonThicknessNoDetour
		default:
			d.detour = float64(best) * pathMoveCardinal
			d.n = SapperThickness(hp, d.detour)
			d.reason = ReasonThicknessDetour
			if d.detour > perimeterTable.DetourBudgetTicks {
				d.reason = ReasonThicknessCapped
			}
		}
		design[i] = d
	}

	// Segments: runs of touching cut cells of one thickness and reason.
	sort.Slice(cut, func(a, b int) bool { return cut[a] < cut[b] })
	done := map[int32]bool{}
	for _, i := range cut {
		if done[i] {
			continue
		}
		seg := CutSegment{Thickness: design[i].n, Reason: design[i].reason, HP: design[i].hp}
		done[i] = true
		stack := []int32{i}
		for len(stack) > 0 {
			u := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			seg.Cells = append(seg.Cells, g.cell(u))
			seg.DetourTicks = math.Max(seg.DetourTicks, design[u].detour)
			for _, d := range neighbours8 {
				c := addCell(g.cell(u), d)
				if j, ok := g.idx(c); ok && inCut[j] && !done[j] && design[j].n == seg.Thickness && design[j].reason == seg.Reason {
					done[j] = true
					stack = append(stack, j)
				}
			}
		}
		sort.Slice(seg.Cells, func(a, b int) bool { return cellLess(seg.Cells[a], seg.Cells[b]) })
		out.Segments = append(out.Segments, seg)
	}

	// The walls: the cut, thickened outward by each cell's thickness.
	walls := map[domain.Cell]bool{}
	for _, i := range cut {
		c := g.cell(i)
		walls[c] = true
		r := int32(design[i].n - 1)
		for dx := -r; dx <= r; dx++ {
			for dz := -r; dz <= r; dz++ {
				if j, ok := g.idx(domain.Cell{X: c.X + dx, Z: c.Z + dz}); ok && outside[j] && g.cost[j] > 0 && (in.keepOffWalls == nil || !in.keepOffWalls[j]) {
					walls[g.cell(j)] = true
				}
			}
		}
	}
	for _, i := range cut {
		out.Cut = append(out.Cut, g.cell(i))
	}
	out.Walls = sortedCells(walls)

	verdict, reason := EmbedPlace, ReasonCutTraced
	if len(out.Unclosable) > 0 {
		verdict, reason = EmbedUnplaceable, ReasonCutOpen
	}
	out.Decisions = append(out.Decisions, PerimeterCutDecision{Verdict: verdict, Reason: reason, Attrs: map[string]any{"cut": len(out.Cut), "walls": len(out.Walls), "unclosable": len(out.Unclosable), "cost": flow}})
	for k, seg := range out.Segments {
		out.Decisions = append(out.Decisions, PerimeterCutDecision{Verdict: EmbedPlace, Reason: seg.Reason, Attrs: map[string]any{"segment": k, "cells": len(seg.Cells), "thickness": seg.Thickness, "detour_ticks": seg.DetourTicks, "hp": seg.HP}})
	}
	return out
}

// PlanPerimeterCut advises the routing cut around the plan's core enclosure
// over the survey: the base is the enclosure (the core and its yard, the
// killbox included), the way in the plan's killbox opening, its channel and
// approach lane. A plan without rooms or an enclosure has no cut. The wall's
// strength sets the thickness.
func PlanPerimeterCut(plan LayoutPlan, s MapSurvey, wall PerimeterWall) PerimeterCut {
	w, h := s.Bounds.Width, s.Bounds.Height
	if len(plan.Rooms) == 0 || w < 1 || h < 1 {
		return PerimeterCut{}
	}
	enc := coreEnclosure(plan, w, h)
	if enc.bbox.Width == 0 {
		return PerimeterCut{}
	}
	g := newCutGrid(w, h)
	in := cutInput{grid: g, hp: make([]float64, w*h), hardToWall: make([]bool, w*h), keepOffWalls: make([]bool, w*h)}
	firm, light := wall.HP.Value()
	lightHP, lightKnown := wall.LightHP.Value()
	if !light {
		firm = 0
	}
	if !lightKnown {
		lightHP = 0
	}
	cells := make([]SurveyCell, w*h)
	read := make([]bool, w*h)
	for _, c := range s.Cells {
		if i, ok := g.idx(c.Cell); ok {
			cells[i], read[i] = c, true
		}
	}
	for i := int32(0); i < w*h; i++ {
		sc := cells[i]
		if read[i] && (sc.Rock || sc.Hazard || !sc.Walkable && !sc.Built) {
			g.barrier[i] = true
			continue
		}
		switch {
		case !read[i] || sc.Footing == FootingFirm:
			g.cost[i], in.hp[i] = 1, firm
		case sc.Footing == FootingLight:
			g.cost[i], in.hp[i] = 1, lightHP
		case sc.Bridgeable:
			g.cost[i], in.hp[i] = 2, lightHP
		default:
			g.cost[i], in.hardToWall[i] = cutGap, true
		}
		x, z := i%w, i/w
		g.source[i] = x == 0 || z == 0 || x == w-1 || z == h-1
	}
	for i := range g.sink {
		if enc.in[i] && !g.barrier[i] {
			g.sink[i], g.cost[i], g.source[i] = true, 0, false
		}
	}

	// The opening: its channel through the ring, the lane outside it.
	if k, ok := KillboxOpening(plan); ok {
		d := directionOf(k.Toward)
		p := perpendicular(d)
		for t := int32(0); t < perimeterThick; t++ {
			for a := int32(-1); a <= 1; a++ {
				if i, ok := g.idx(addCell(addCell(k.Entry, scale(d, t)), scale(p, a))); ok {
					g.barrier[i], in.keepOffWalls[i] = true, true
				}
			}
		}
		if i, ok := g.idx(k.Entry); ok {
			in.mouth, in.hasOpening = i, true
		}
		for _, a := range plan.LiveSites(NodeApproach) {
			for _, c := range a.Footprint.Cells() {
				if i, ok := g.idx(c); ok && !g.barrier[i] && !g.sink[i] {
					g.cost[i], g.source[i], in.keepOffWalls[i] = 0, true, true
				}
			}
		}
	}
	in.grid = g
	return designCut(in)
}

// PerimeterCutDecisions are the cut's decisions, for the caller to journal.
func PerimeterCutDecisions(c PerimeterCut) []PerimeterCutDecision { return c.Decisions }
