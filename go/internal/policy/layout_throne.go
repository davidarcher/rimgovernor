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
		if r.Role == PlannedThrone && int(r.Region.Bounds().Width)*int(r.Region.Bounds().Height) >= area {
			return r, true
		}
	}
	return PlannedRoom{}, false
}
