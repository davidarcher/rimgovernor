package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Cooling for the standing tomb, morgue and meal closet (#840, #936, #1820,
// #2185). Once the colony can build coolers, each of those rooms that stands
// and measures above TombMaxC owes cooling, empty or not: cold keeps a
// colonist fresh for resurrector mech serum and slows rot, and the room is
// cooled before anything lies in it. MaintainRefrigeration takes the room
// beside its food rooms, reuses or builds a cooler (the planned one over the
// room's exhaust reservation first) and sets it to FreezerTargetC. Cooling
// never gates a room's shell.

// TombMaxC is the warmest a standing cooling room may measure before it owes
// cooling: corpses stop rotting at freezing.
const TombMaxC = 0.0

// WarmCoolingRooms lists the native room IDs of the standing planned tombs,
// morgues and meal closets that measure warmer than TombMaxC, sorted. Known
// empty while coolers are unavailable; unknown while a fact it reads is.
func WarmCoolingRooms(coolers domain.Fact[bool], plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation]) domain.Fact[[]string] {
	available, ak := coolers.Value()
	if ak && !available {
		return domain.Known[[]string](nil)
	}
	p, pk := plan.Value()
	r, rk := rooms.Value()
	if !ak || !pk || !rk {
		return domain.Unknown[[]string]()
	}
	var out []string
	for _, planned := range p.AllRooms() {
		if planned.Role != PlannedTomb && planned.Role != PlannedMorgue && planned.Role != PlannedMealCloset {
			continue
		}
		// Census: the room's cells and floors are cooled.
		room, ok := CensusRoomIn(planned, r)
		if !ok {
			continue
		}
		t, known := room.Temperature.Value()
		if !known {
			return domain.Unknown[[]string]()
		}
		if t > TombMaxC {
			out = append(out, room.ID)
		}
	}
	sort.Strings(out)
	return domain.Known(out)
}

// WithWarmRooms adds the warm cooling rooms to the review's rooms: any warm
// one makes it active. Unknown rooms leave the review as it is.
func (r RefrigerationReview) WithWarmRooms(warm domain.Fact[[]string]) RefrigerationReview {
	ids, known := warm.Value()
	if !known || len(ids) == 0 {
		return r
	}
	seen := map[string]bool{}
	for _, id := range r.Rooms {
		seen[id] = true
	}
	for _, id := range ids {
		if !seen[id] {
			r.Rooms = append(r.Rooms, id)
		}
	}
	sort.Strings(r.Rooms)
	r.Active, r.Warm = true, ids
	return r
}
