package policy

import (
	"maps"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Replans on the new generator. A replan runs the
// generator twice over the saved plan.
//
// Growth: every room and wing the plan holds is pinned, so the generator
// only adds what the plan lacks (a missing base room, a bedroom wing for
// new colonists, a suite block, a tomb). Nothing moves, and a plan that
// needed nothing is returned as it came.
//
// Re-siting: the rooms nothing of ours stands on (the unpinned ones) are
// lifted out and sited again, hallways kept, with the small replan search
// budget. The candidate replaces the plan only when its score gain clears
// planWeights.ReplanGain, or when it passes the hard tier and the plan does
// not, so a plan does not flicker between near-equal layouts every hour.
// Pinned rooms (FixedRooms) keep their Interior, Door and Doors.

// replanSearchIters is the local-search operators one hourly replan runs
// (siteSearchIters is the fresh-siting budget). The count is the budget:
// wall time never changes a plan.
const replanSearchIters = 12

// replanner is the grid, scorer and demands one replan shares.
type replanner struct {
	g     coreGrid
	sc    planScorer
	pawns int
	tombs int
	tier  TechTier
	suite []float64
}

// newReplanner prepares a replan of plan over survey s. The core ground is
// plan.Zones less the blocked cells, and plan's reservations (the
// perimeter's nodes, laid again afterwards, are not reservations).
func newReplanner(plan LayoutPlan, s MapSurvey, blocked map[domain.Cell]bool, pawns, tombs int, tier TechTier, suites []float64) replanner {
	g := newCoreGrid(coreWithout(plan.Zones, blocked), plan.Reservations).withSoil(s)
	return replanner{g: g, sc: planScorer{g: g, s: s, ground: newSiteGround(s)}, pawns: pawns, tombs: tombs, tier: tier, suite: suites}
}

// site generates plan again with pins kept where they are, searched for
// iters operators. A plan with no hallway and no core ground to seed one is
// returned as it is.
func (rp replanner) site(plan LayoutPlan, pins map[int32]bool, iters int) LayoutPlan {
	g := rp.g
	g.fixed, g.noShelter = pins, true
	var seed domain.Cell
	if len(plan.Spine) > 0 {
		seed = plan.Spine[0].From
	} else if c, ok := g.seed(); ok {
		seed = c
	} else {
		return plan
	}
	base := g.generateBase(resiteInput(plan, pins), seed, rp.pawns, rp.tombs, rp.tier, rp.suite...)
	if iters > 0 {
		base = g.search(rp.sc, base, seed, iters)
	}
	return g.finish(base)
}

// better reports whether cand should replace old: it houses at least as
// many colonists, and either passes the hard tier where old fails, or
// beats old's score by planWeights.ReplanGain.
func (rp replanner) better(old, cand LayoutPlan) bool {
	if bedroomCount(cand) < min(bedroomCount(old), rp.pawns) {
		return false
	}
	os, cs := rp.sc.core(old), rp.sc.core(cand)
	if os.Passes() != cs.Passes() {
		return cs.Passes()
	}
	return cs.Total()-os.Total() >= planWeights.ReplanGain
}

func bedroomCount(p LayoutPlan) int {
	n := 0
	for _, r := range p.AllRooms() {
		if r.Role == PlannedBedroom {
			n++
		}
	}
	return n
}

// allPins is every room of plan, wings included.
func allPins(plan LayoutPlan) map[int32]bool {
	pins := map[int32]bool{}
	for _, r := range plan.AllRooms() {
		pins[r.ID] = true
	}
	return pins
}

// ReplanPins widens fixed (the rooms with anything of ours on them) to what
// a replan must not move (every room when fixed is nil, the census unknown):
// the partner a fixed room's Link door opens on, a
// bedroom wing with a fixed room (a wing is sited as one block), and every
// suite block (existing suites never move). An emptied Retiring wing is
// never pinned, built or not: it is the ground a replan may reclaim.
func ReplanPins(plan LayoutPlan, fixed map[int32]bool, emptied map[domain.Cell]bool, tier TechTier) map[int32]bool {
	if fixed == nil {
		// The census is unknown, so nothing is known to be unbuilt.
		return allPins(plan)
	}
	pins := maps.Clone(fixed)
	// A room and the successor that replaces it stay as sited until the
	// successor stands and the old room leaves the plan.
	maps.Copy(pins, successorPinned(plan))
	for changed := true; changed; {
		changed = false
		for _, r := range plan.Rooms {
			for _, o := range plan.Rooms {
				if pins[r.ID] && !pins[o.ID] && plan.linked(r, o) || pins[o.ID] && !pins[r.ID] && plan.linked(o, r) {
					pins[r.ID], pins[o.ID] = true, true
					changed = true
				}
			}
		}
	}
	for _, w := range plan.Wings {
		if emptied[w.Corridor.From] && retireWings([]Wing{w}, tier)[0].Purpose == WingBedroomsRetiring {
			for _, r := range w.Rooms {
				delete(pins, r.ID)
			}
			continue
		}
		if w.Purpose == WingSuites || wingPinned(w, pins) {
			for _, r := range w.Rooms {
				pins[r.ID] = true
			}
		}
	}
	return pins
}

// linked reports one of a's link doors on b's walls.
func (p LayoutPlan) linked(a, b PlannedRoom) bool {
	return a.Region.Bounds() != b.Region.Bounds() && slices.ContainsFunc(p.LinksOf(a), func(l domain.Cell) bool { return contains(roomWalls(b), l) })
}

// wingPinned reports a pinned room in w: the wing stays as sited.
func wingPinned(w Wing, pins map[int32]bool) bool {
	for _, r := range w.Rooms {
		if pins[r.ID] {
			return true
		}
	}
	return false
}

// resiteInput is plan with the rooms a replan may move lifted out: the
// unpinned base rooms and the bedroom wings without a pinned room. Add-on
// rooms and suite blocks stay where they are.
func resiteInput(plan LayoutPlan, pins map[int32]bool) LayoutPlan {
	var rooms []PlannedRoom
	for _, r := range plan.Rooms {
		if pins[r.ID] || !searchRole(r.Role) {
			rooms = append(rooms, r)
		}
	}
	var wings []Wing
	for _, w := range plan.Wings {
		if w.Purpose == WingSuites || wingPinned(w, pins) {
			wings = append(wings, w)
		}
	}
	plan.Rooms, plan.Wings = rooms, wings
	return plan
}

// canResite reports a room or bedroom wing of plan that pins leave free.
func canResite(plan LayoutPlan, pins map[int32]bool) bool {
	for _, r := range plan.Rooms {
		if !pins[r.ID] && searchRole(r.Role) {
			return true
		}
	}
	for _, w := range plan.Wings {
		if w.Purpose != WingSuites && !wingPinned(w, pins) {
			return true
		}
	}
	return false
}

// replanCore grows plan around every room it holds, then re-sites the
// unpinned ones (see the file comment). It reports whether the plan changed.
func (rp replanner) replanCore(plan LayoutPlan, pins map[int32]bool) (LayoutPlan, bool) {
	changed := false
	grown := rp.site(plan, allPins(plan), 0)
	if len(grown.AllRooms()) != len(plan.AllRooms()) || len(grown.Wings) != len(plan.Wings) {
		plan, changed = grown, true
	}
	if !canResite(plan, pins) {
		return plan, changed
	}
	if cand := rp.site(plan, pins, replanSearchIters); rp.better(plan, cand) {
		return cand, true
	}
	return plan, changed
}
