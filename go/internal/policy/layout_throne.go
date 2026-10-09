package policy

import (
	"sort"
)

// The throne room: one core room sized from the
// next title's minimum area (RoyalRung.ThroneMinArea), sited only once a
// colonist holds or can claim a title that asks for one. It is the
// sleeping planner's under MaintainHousing (NextThroneStep).

// PlannedThrone is the throne room's plan role.
const PlannedThrone PlannedRole = "throne"

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
func (p LayoutPlan) ThroneRoomFor(area int) (PlannedRoom, bool) {
	for _, r := range p.AllRooms() {
		if r.Role == PlannedThrone && int(r.Interior.Width)*int(r.Interior.Height) >= area {
			return r, true
		}
	}
	return PlannedRoom{}, false
}

// growThroneRoom adds a throne room of at least area cells to plan unless
// it holds one, at the nearest core slot like any other core room. Existing
// rooms are only retired by the reconcile steps in layout_retire.go and none
// moves or resizes: a room a later title outgrows stays until a larger one
// supersedes it. It reports whether a room was added; a plan with no
// core ground or no slot for any shape is left as it is, with the error.
func growThroneRoom(plan LayoutPlan, area int, sc *planScorer) (LayoutPlan, bool, error) {
	if area <= 0 || len(plan.Hallways()) == 0 {
		return plan, false, nil
	}
	if _, ok := plan.ThroneRoomFor(area); ok {
		return plan, false, nil
	}
	return SiteRoom(plan, sc, PlannedThrone, ThroneRoomSizes(area)...)
}
