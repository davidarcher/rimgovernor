package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Plan scoring (#1952, epic #1938): one generator-independent score for a
// core plan, read by every siting search. It is tiered. The hard tier is
// pass or fail: every base room placed (coreBaseRooms), CheckRoutes valid,
// and no rich soil under a room or hallway. A plan failing it ranks below
// any plan that passes (PlanScore.Better), so rich soil is a loss only when
// nothing avoids it: when every candidate stands on some, they all share
// the lower tier and the soft score picks among them. The soft tier is the
// sum of per-term values, higher better, every weight in planWeights.

// planWeights is the one weight table. The replay harness (#1953) tunes it.
var planWeights = struct {
	// Hard tier, charged once per failure so failing plans still rank
	// (more missing rooms lower) inside their tier.
	MissingRoom, BadRoutes int
	// Soil: per soilCost point under a room or hallway, and per rock cell
	// (a dig cost).
	Soil, Rock int
	// Walk: per cell of weighted door-to-door path (routeTrips weights);
	// WalkUnreachable stands in for a trip with no path.
	Walk, WalkUnreachable int
	// Footprint per cell the ring would enclose; Wall per built wall cell.
	Footprint, Wall int
	// Edge: per step a room cell stands inside EdgeClear walking distance
	// of an open map edge cell (raiders arrive there).
	Edge, EdgeClear int
	// Defense: a killbox placed, an approach lane placed, and per ring cell
	// nothing can close.
	Killbox, Approach, Gap int
	// Expansion: per free core cell within ExpansionReach of the plan,
	// at most ExpansionCap cells counted.
	Expansion, ExpansionReach, ExpansionCap int
	// ReplanGain is the score a replan's candidate must beat the saved
	// plan's by before the unbuilt rooms are sited again (#1958): about a
	// room's worth of plain soil, so near-equal layouts never flicker.
	ReplanGain int
}{
	MissingRoom: 100000, BadRoutes: 100000,
	Soil: 3, Rock: 1,
	// Walk is 0 until the replay harness (#1953) tunes it: weighted on, the
	// base packs into the thin-roof lab's pocket and the first turbine has no
	// rock to dig (TestThinRoofMountainLabPlansTurbineOnRockBesidePocket).
	Walk: 0, WalkUnreachable: 200,
	Footprint: 1, Wall: 10,
	Edge: 3, EdgeClear: 50,
	Killbox: 200, Approach: 100, Gap: 20,
	Expansion: 1, ExpansionReach: 6, ExpansionCap: 300,
	ReplanGain: 100,
}

// PlanScore is a plan's per-term values and tier verdict. The soft terms
// are signed contributions to Total (costs negative).
type PlanScore struct {
	// Hard tier.
	Missing   []ModuleRole // base rooms the plan lacks
	RoutesErr string       // CheckRoutes' error, "" when the routes are valid
	RichCells int          // rich-soil cells under rooms and hallways
	// Soft tier.
	Soil, Walk, Footprint, Wall, Edge, Defense, Expansion int
	// Walled reports whether Wall and Defense were scored (PlanPerimeter
	// ran); a cheap score leaves them zero.
	Walled bool
}

// Passes is the hard-tier verdict.
func (s PlanScore) Passes() bool {
	return len(s.Missing) == 0 && s.RoutesErr == "" && s.RichCells == 0
}

// Total is the soft terms plus the hard-tier failure charges.
func (s PlanScore) Total() int {
	t := s.Soil + s.Walk + s.Footprint + s.Wall + s.Edge + s.Defense + s.Expansion
	t -= planWeights.MissingRoom * len(s.Missing)
	if s.RoutesErr != "" {
		t -= planWeights.BadRoutes
	}
	return t
}

// Better reports whether s ranks above o: a passing plan above a failing
// one, then the higher Total.
func (s PlanScore) Better(o PlanScore) bool {
	if a, b := s.Passes(), o.Passes(); a != b {
		return a
	}
	return s.Total() > o.Total()
}

func (s PlanScore) String() string {
	return fmt.Sprintf("pass=%v total=%d missing=%d routes=%q rich=%d soil=%d walk=%d footprint=%d wall=%d edge=%d defense=%d expansion=%d",
		s.Passes(), s.Total(), len(s.Missing), s.RoutesErr, s.RichCells, s.Soil, s.Walk, s.Footprint, s.Wall, s.Edge, s.Defense, s.Expansion)
}

// Score scores plan over the survey s: every term, the wall and defense
// terms included (PlanPerimeter walls a copy). Deterministic.
func Score(plan LayoutPlan, s MapSurvey) PlanScore {
	sc := newPlanScorer(plan.Zones, plan.Reservations, s)
	return sc.walled(plan, sc.core(plan))
}

// planScorer holds the per-survey state every candidate's score shares.
type planScorer struct {
	g      coreGrid
	s      MapSurvey
	ground siteGround
}

func newPlanScorer(zones []LayoutZone, reserved []LayoutReservation, s MapSurvey) planScorer {
	return planScorer{g: newCoreGrid(zones, reserved).withSoil(s), s: s, ground: newSiteGround(s)}
}

// core scores every term that needs no wall: the hard tier, soil and rock,
// walking distance, footprint, the map edge and expansion room.
func (sc planScorer) core(p LayoutPlan) PlanScore {
	var out PlanScore
	rooms := p.AllRooms()
	have := map[ModuleRole]bool{}
	for _, r := range rooms {
		have[r.Role] = true
	}
	for _, role := range coreBaseRooms {
		if !have[role] {
			out.Missing = append(out.Missing, role)
		}
	}
	if len(rooms) == 0 {
		return out
	}
	if _, err := CheckRoutes(p); err != nil {
		out.RoutesErr = err.Error()
	}

	under := map[domain.Cell]bool{}
	for _, r := range rooms {
		for _, c := range rectCells(r.Interior) {
			under[c] = true
		}
	}
	for _, h := range p.Hallways() {
		for _, c := range rectCells(pad(rectOf(h.From, h.To), SpineWidth/2)) {
			under[c] = true
		}
	}
	for c := range under {
		soil := sc.g.soil[c]
		out.Soil -= planWeights.Soil * soil
		if soil == soilCostRich {
			out.RichCells++
		}
		if sc.g.rock[c] {
			out.Soil -= planWeights.Rock
		}
	}

	out.Walk = -planWeights.Walk * planWalk(p)
	w, h := sc.s.Bounds.Width, sc.s.Bounds.Height
	for _, in := range coreFootprint(p, w, h) {
		if in {
			out.Footprint -= planWeights.Footprint
		}
	}
	out.Edge = -sc.edgeCost(rooms)
	out.Expansion = sc.expansion(rooms, under)
	return out
}

// walled adds the terms read off PlanPerimeter's wall: wall length and
// defensibility (killbox, approach lane, gaps).
func (sc planScorer) walled(p LayoutPlan, out PlanScore) PlanScore {
	out.Walled = true
	if len(p.AllRooms()) == 0 {
		return out
	}
	wp := PlanPerimeter(p, sc.s)
	walls := map[domain.Cell]bool{}
	var killbox, approach bool
	gaps := 0
	for _, r := range wp.Reservations {
		switch r.Kind {
		case ReservePerimeter, ReservePerimeterLight, ReserveBridge:
			for _, c := range rectCells(r.Area) {
				walls[c] = true
			}
		case ReserveKillbox:
			killbox = true
		case ReserveKillboxApproach:
			approach = true
		case ReservePerimeterGap:
			gaps += int(r.Area.Width * r.Area.Height)
		}
	}
	out.Wall = -planWeights.Wall * len(walls)
	if killbox {
		out.Defense += planWeights.Killbox
	}
	if approach {
		out.Defense += planWeights.Approach
	}
	out.Defense -= planWeights.Gap * gaps
	return out
}

// planWalk is the affinity-weighted walking distance over p: for every
// routeTrips edge, each room of the from role walks the shortest path over
// the hallway graph to the nearest to end, so every bedroom of a wing
// counts its own distance to dining. A trip with no path costs
// WalkUnreachable; one whose ends the plan lacks costs nothing. Unweighted:
// the Walk term applies planWeights.Walk.
func planWalk(p LayoutPlan) int {
	walk, rooms := routeWalk(p)
	cells := tripCells(p, rooms)
	total := 0
	for _, trip := range routeTrips {
		to, froms := cells(trip.to), cells(trip.from)
		if len(to) == 0 || len(froms) == 0 {
			continue
		}
		var goal []domain.Cell
		for _, g := range to {
			goal = append(goal, g...)
		}
		dist := routeDist(walk, goal)
		for _, from := range froms {
			best := -1
			for _, c := range from {
				if d, ok := dist[c]; ok && (best < 0 || d < best) {
					best = d
				}
			}
			if best < 0 {
				best = planWeights.WalkUnreachable
			}
			total += trip.weight * best
		}
	}
	return total
}

// routeDist is every walk cell's 4-neighbour step distance to the nearest
// of goal.
func routeDist(walk map[domain.Cell]int, goal []domain.Cell) map[domain.Cell]int {
	dist := map[domain.Cell]int{}
	var queue []domain.Cell
	for _, c := range goal {
		if _, ok := walk[c]; ok {
			if _, seen := dist[c]; !seen {
				dist[c] = 0
				queue = append(queue, c)
			}
		}
	}
	for head := 0; head < len(queue); head++ {
		c := queue[head]
		for _, d := range [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			n := domain.Cell{X: c.X + d[0], Z: c.Z + d[1]}
			if _, ok := walk[n]; !ok {
				continue
			}
			if _, seen := dist[n]; seen {
				continue
			}
			dist[n] = dist[c] + 1
			queue = append(queue, n)
		}
	}
	return dist
}

// edgeCost charges every room cell planWeights.Edge per step of walking
// distance (siteGround.walk) it stands closer to an open map edge cell than
// EdgeClear, or a fifth of the map's short side on a small map. The ring
// cannot be built in the edge margin and raiders arrive at the edge. Rock
// and other blocked cells lengthen the path, so a core tucked into a
// mountain is as far as a raider must walk around it; a cell no raider can
// reach costs nothing.
func (sc planScorer) edgeCost(rooms []LayoutRoom) int {
	b := sc.s.Bounds
	clear := min(planWeights.EdgeClear, int(min(b.Width, b.Height))/5)
	cost := 0
	for _, r := range rooms {
		for _, c := range rectCells(r.Interior) {
			d := sc.ground.walkDist(c)
			if d < 0 || d >= clear {
				continue
			}
			cost += planWeights.Edge * (clear - d)
		}
	}
	return cost
}

// expansion is the free ground a plan can still grow into: core cells
// within ExpansionReach of a room that are neither under the plan, rock nor
// rich soil, at most ExpansionCap of them.
func (sc planScorer) expansion(rooms []LayoutRoom, under map[domain.Cell]bool) int {
	near := map[domain.Cell]bool{}
	for _, r := range rooms {
		for _, c := range rectCells(pad(r.Interior, int32(planWeights.ExpansionReach))) {
			if sc.g.core[c] && !under[c] && !sc.g.rock[c] && sc.g.soil[c] != soilCostRich {
				near[c] = true
			}
		}
	}
	return planWeights.Expansion * min(len(near), planWeights.ExpansionCap)
}
