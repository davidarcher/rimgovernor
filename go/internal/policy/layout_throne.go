package policy

// The throne room (#1601, epic #1598): one core room sized from the
// next title's minimum area (RoyalRung.ThroneMinArea), sited only once a
// colonist holds or can claim a title that asks for one. It is the
// sleeping planner's under MaintainHousing (NextThroneStep).

// ModuleThrone is the throne room's plan role.
const ModuleThrone ModuleRole = "throne"

// throneMinDepth is the shallowest throne room interior.
const throneMinDepth = 5

// ThroneRoomSize is the throne room interior (width along the spine, depth
// away from it) holding at least area cells: the depth nearest two thirds
// of the width within throneMinDepth..coreMaxDepth, then the width that
// covers the area. The room count (a title's area requirement) is the
// interior's cell count, walls excluded.
func ThroneRoomSize(area int) [2]int32 {
	best := [2]int32{}
	for d := int32(throneMinDepth); d <= coreMaxDepth; d++ {
		w := max(int32((area+int(d)-1)/int(d)), d)
		if best == ([2]int32{}) || abs32(2*w-3*d) < abs32(2*best[0]-3*best[1]) {
			best = [2]int32{w, d}
		}
	}
	return best
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
// core ground or no slot is left as it is.
func growThroneRoom(plan LayoutPlan, area int) (LayoutPlan, bool) {
	if area <= 0 || len(plan.Spine) == 0 {
		return plan, false
	}
	if _, ok := plan.ThroneRoomFor(area); ok {
		return plan, false
	}
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
	size := ThroneRoomSize(area)
	spine, rooms, placed, _ := g.placeRole(append([]SpineSegment(nil), plan.Spine...), append([]LayoutRoom(nil), plan.Rooms...), plan.Wings, ModuleThrone, size)
	if !placed {
		return plan, false
	}
	plan.Spine, plan.Rooms = spine, rooms
	return plan, true
}
