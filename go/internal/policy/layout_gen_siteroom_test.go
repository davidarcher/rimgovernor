package policy

import (
	"fmt"
	"testing"
)

// SiteRoom is a fixture of the old generator's tests; production sites every
// add-on room through the station chain.

// siteRoomCandidates is how many fitting slots SiteRoom scores before it
// keeps the best.
const siteRoomCandidates = 12

// SiteRoom sites an add-on room of the role onto plan: the best-scoring of
// the next fitting core slots, trying sizes (interior width, depth) in order,
// kept off the wings' ground and any weapon clearance. A plan with no core
// ground or no slot for any size is returned as it is, with an error naming
// the room that could not be placed and why.
//
// Slots are scored as the initial siting scores a plan (planScorer.core: soil,
// rock to dig, the walk from the spawn edge through the dug rock, footprint,
// room to grow), so a room does not pop through a mountain's face that the
// first plan stayed behind. With no scorer the nearest slot is taken.
func SiteRoom(plan LayoutPlan, sc *planScorer, role PlannedRole, sizes ...[2]int32) (LayoutPlan, bool, error) {
	return siteRoomWhere(plan, sc, role, nil, sizes...)
}

// siteRoomWhere is SiteRoom keeping only the slots whose room accept takes
// (nil takes all): a successor that must keep its Link door refuses the slots
// that have none.
func siteRoomWhere(plan LayoutPlan, sc *planScorer, role PlannedRole, accept func(PlannedRoom) bool, sizes ...[2]int32) (LayoutPlan, bool, error) {
	g := newCoreGrid(plan.Zones, plan.Reserved())
	if len(g.core) == 0 {
		return plan, false, fmt.Errorf("the plan needs a %s room but the map survey left no core ground to place it on", role)
	}
	// Keep off the wings' ground, as the generator does.
	g.carveSuiteWings(plan.Wings)
	g.carveBedroomWings(plan.Wings)
	clear := weaponClearance(plan, role)
	for _, r := range clear {
		g.carve(r)
	}
	limit := siteRoomCandidates
	if sc == nil {
		limit = 1
	}
	g.skip = map[Rectangle]bool{}
	g.linkBehind = accept != nil
	var best LayoutPlan
	var bestScore PlanScore
	found := false
	for range limit {
		var spine []SpineSegment
		var rooms []PlannedRoom
		placed := false
		for _, size := range sizes {
			spine, rooms, placed, _ = g.packRoom(append([]SpineSegment(nil), plan.Spine...), append([]PlannedRoom(nil), plan.Rooms...), plan.Wings, role, size)
			if placed {
				break
			}
		}
		if !placed {
			break
		}
		cand := plan
		cand.Spine, cand.Rooms = spine, rooms
		g.skip[rooms[len(rooms)-1].Region.Bounds()] = true
		if accept != nil && !accept(rooms[len(rooms)-1]) {
			continue
		}
		if sc == nil {
			return cand, true, nil
		}
		if score := sc.core(cand); !found || score.Better(bestScore) {
			best, bestScore, found = cand, score, true
		}
	}
	if found {
		return best, true, nil
	}
	if len(clear) > 0 {
		return plan, false, fmt.Errorf("the plan needs a %s room but no free core slot clear of the weapon clearance round the plan's armory and prisons fits one (interior sizes tried: %v)", role, sizes)
	}
	return plan, false, fmt.Errorf("the plan needs a %s room but no free core slot fits one (interior sizes tried: %v)", role, sizes)
}

// SiteRoom scores the next fitting slots as the initial siting does and keeps
// the best, so it never ranks below the nearest slot it would otherwise take.
func TestSiteRoomKeepsTheBestScoringSlot(t *testing.T) {
	p, s := scoredPlan(t)
	sc := newPlanScorer(p.Zones, p.Reservations, s)
	nearest, ok, err := SiteRoom(p, nil, PlannedThrone, [2]int32{6, 5})
	if !ok || err != nil {
		t.Fatal("nearest slot:", ok, err)
	}
	best, ok, err := SiteRoom(p, &sc, PlannedThrone, [2]int32{6, 5})
	if !ok || err != nil {
		t.Fatal("scored slot:", ok, err)
	}
	if len(best.Rooms) != len(nearest.Rooms) {
		t.Fatal("rooms added", len(best.Rooms)-len(p.Rooms), "want", len(nearest.Rooms)-len(p.Rooms))
	}
	if sc.core(nearest).Better(sc.core(best)) {
		t.Fatal("scored siting ranks below the nearest slot:", sc.core(best), sc.core(nearest))
	}
}
