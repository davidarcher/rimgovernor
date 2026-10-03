package policy

import "sort"

// The throne room (#1601, epic #1598): one core room sized from the
// next title's minimum area (RoyalRung.ThroneMinArea), sited only once a
// colonist holds or can claim a title that asks for one. It is the
// sleeping planner's under MaintainHousing (NextThroneStep).

// ModuleThrone is the throne room's plan role.
const ModuleThrone ModuleRole = "throne"

// throneMinSide is the shortest throne room side.
const throneMinSide int32 = 4

// ThroneRoomSizes are the throne room interiors (width along the spine,
// depth away from it) holding at least area cells, best first: the fewest
// cells, then the aspect nearest two thirds (wider than deep). Depth stays
// within the core's cross-section (coreMaxDepth) and no room is more than
// half again the area. A crowded plan takes the next shape when the best
// finds no slot. The area is the interior's cell count, walls excluded.
func ThroneRoomSizes(area int) [][2]int32 {
	var out [][2]int32
	limit := max(area*3/2, int(throneMinSide*throneMinSide))
	for d := throneMinSide; d <= coreMaxDepth; d++ {
		w := max(int32((area+int(d)-1)/int(d)), throneMinSide)
		if int(w)*int(d) <= limit {
			out = append(out, [2]int32{w, d})
		}
	}
	if len(out) == 0 {
		w := int32((area + int(coreMaxDepth) - 1) / int(coreMaxDepth))
		out = append(out, [2]int32{w, coreMaxDepth})
	}
	sort.Slice(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if ca, cb := a[0]*a[1], b[0]*b[1]; ca != cb {
			return ca < cb
		}
		return abs32(2*a[0]-3*a[1]) < abs32(2*b[0]-3*b[1])
	})
	return out
}

// ThroneRoomFor is the plan's first throne room whose interior holds at
// least area cells (the room a title's area requirement is met in).
func (p LayoutPlan) ThroneRoomFor(area int) (LayoutRoom, bool) {
	for _, r := range p.AllRooms() {
		if r.Role == ModuleThrone && int(r.Interior.Width)*int(r.Interior.Height) >= area {
			return r, true
		}
	}
	return LayoutRoom{}, false
}

// growThroneRoom adds a throne room of at least area cells to plan unless
// it holds one, at the nearest core slot like any other core room. Existing
// rooms never move or shrink: a room a later title outgrows stays and a
// larger one is added. It reports whether a room was added; a plan with no
// core ground or no slot for any shape is left as it is.
func growThroneRoom(plan LayoutPlan, area int) (LayoutPlan, bool) {
	if area <= 0 || len(plan.Spine) == 0 {
		return plan, false
	}
	if _, ok := plan.ThroneRoomFor(area); ok {
		return plan, false
	}
	return growModuleRoom(plan, ModuleThrone, ThroneRoomSizes(area))
}

// growModuleRoom adds a room of the role to plan at the nearest core slot,
// trying sizes (interior width, depth) in order, like any other core room. A
// plan with no core ground or no slot for any size is left as it is.
func growModuleRoom(plan LayoutPlan, role ModuleRole, sizes [][2]int32) (LayoutPlan, bool) {
	g := newCoreGrid(plan.Zones, plan.Reservations)
	if len(g.core) == 0 {
		return plan, false
	}
	// Keep off the wings' ground and growth reserve, as Grow does.
	if i := wingOf(plan.Wings, WingSuites); i >= 0 {
		g.carve(wingReserve(plan.Wings[i], plan.SuiteRooms()))
	}
	bedrooms := 0
	for _, i := range bedroomWings(plan.Wings) {
		bedrooms += len(plan.Wings[i].Rooms)
	}
	g.carveBedroomWings(plan.Wings, bedrooms)
	for _, size := range sizes {
		spine, rooms, placed, _ := g.placeRole(append([]SpineSegment(nil), plan.Spine...), append([]LayoutRoom(nil), plan.Rooms...), plan.Wings, role, size)
		if placed {
			plan.Spine, plan.Rooms = spine, rooms
			return plan, true
		}
	}
	return plan, false
}
