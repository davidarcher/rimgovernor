package policy

import "fmt"

// SiteRoom is the one seam that sites an add-on room of the role onto plan:
// the nearest core slot, trying sizes (interior width, depth) in order, kept
// off the wings' ground and any weapon clearance. Throne, child, gear,
// storage, incinerator and battery rooms all come through it, so a new
// generator replaces this body and nothing else. A plan with no core ground
// or no slot for any size is returned as it is, with an error naming the room
// that could not be placed and why (#1799).
func SiteRoom(plan LayoutPlan, role ModuleRole, sizes ...[2]int32) (LayoutPlan, bool, error) {
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
	for _, size := range sizes {
		spine, rooms, placed, _ := g.placeRole(append([]SpineSegment(nil), plan.Spine...), append([]LayoutRoom(nil), plan.Rooms...), plan.Wings, role, size)
		if placed {
			plan.Spine, plan.Rooms = spine, rooms
			return plan, true, nil
		}
	}
	if len(clear) > 0 {
		return plan, false, fmt.Errorf("the plan needs a %s room but no free core slot clear of the weapon clearance round the plan's armory and prisons fits one (interior sizes tried: %v)", role, sizes)
	}
	return plan, false, fmt.Errorf("the plan needs a %s room but no free core slot fits one (interior sizes tried: %v)", role, sizes)
}
