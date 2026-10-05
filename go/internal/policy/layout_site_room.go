package policy

import "fmt"

// siteRoomCandidates is how many fitting slots SiteRoom scores before it
// keeps the best.
const siteRoomCandidates = 12

// SiteRoom is the one seam that sites an add-on room of the role onto plan:
// the best-scoring of the next fitting core slots, trying sizes (interior
// width, depth) in order, kept off the wings' ground and any weapon
// clearance. Throne, child, gear, storage, incinerator and battery rooms all
// come through it, so a new generator replaces this body and nothing else. A
// plan with no core ground or no slot for any size is returned as it is, with
// an error naming the room that could not be placed and why (#1799).
//
// Slots are scored as the initial siting scores a plan (planScorer.core: soil,
// rock to dig, the walk from the spawn edge through the dug rock, footprint,
// room to grow), so a room does not pop through a mountain's face that the
// first plan stayed behind. With no scorer the nearest slot is taken.
func SiteRoom(plan LayoutPlan, sc *planScorer, role ModuleRole, sizes ...[2]int32) (LayoutPlan, bool, error) {
	g := newCoreGrid(plan.Zones, plan.Reservations)
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
	var best LayoutPlan
	var bestScore PlanScore
	found := false
	for range limit {
		var spine []SpineSegment
		var rooms []LayoutRoom
		placed := false
		for _, size := range sizes {
			spine, rooms, placed, _ = g.packRoom(append([]SpineSegment(nil), plan.Spine...), append([]LayoutRoom(nil), plan.Rooms...), plan.Wings, role, size)
			if placed {
				break
			}
		}
		if !placed {
			break
		}
		cand := plan
		cand.Spine, cand.Rooms = spine, rooms
		g.skip[rooms[len(rooms)-1].Interior] = true
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
