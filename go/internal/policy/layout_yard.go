package policy

import "slices"

// The materials yard: a planned Outdoor room beside the
// core, inside the core ring like the animal pen. It is unfenced open ground
// (PlannedRoom.Unfenced: no ring, gate, roof or floor owed), and the Storage
// department's one yard store covers the whole interior. Its reservation keeps
// the one-cell margin a fence ring would have taken. It never grows: a full
// yard asks for a further one (RoomDemand.Yard).

const (
	// ReserveYard is the yard's reservation: its outline, the interior plus
	// a one-cell margin.
	ReserveYard ReservationKind = "yard"
	// PlannedYard is the plan role of a yard viewed as an Outdoor room.
	PlannedYard PlannedRole = "yard"
	// YardW and YardH are the yard's interior (117 cells).
	YardW int32 = 13
	YardH int32 = 9
)

// YardRooms are the plan's yards as Outdoor rooms, one per reservation in plan
// order.
func (p LayoutPlan) YardRooms() []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.Reservations {
		if r.Kind == ReserveYard {
			out = append(out, PlannedRoom{Role: PlannedYard, Interior: pad(r.Area, -1), Outdoor: true})
		}
	}
	return out
}

// YardRoomsWanted is how many yards the plan should hold: one from the start,
// more when demand asks for them.
func YardRoomsWanted(demand RoomDemand) int { return max(demand.Yard, 1) }

// YardRoomsOwed is the yards demand asks for that plan lacks.
func YardRoomsOwed(plan LayoutPlan, demand RoomDemand) int {
	return max(YardRoomsWanted(demand)-len(plan.YardRooms()), 0)
}

// PlanYardSites tops plan up to want yards, each on the free core-side ground
// nearest the core. Nothing placed moves; a yard that fits nowhere is left
// out.
func PlanYardSites(plan LayoutPlan, want int) LayoutPlan {
	have := len(plan.YardRooms())
	if have >= want || len(plan.Hallways()) == 0 {
		return plan
	}
	plan.Reservations = slices.Clone(plan.Reservations)
	u := newUtilityGrid(plan)
	w, h := walledSide(YardW, YardH)
	for ; have < want; have++ {
		site, ok := u.site(w, h, false, false, u.cx, u.cz)
		if !ok {
			break
		}
		u.reserve(&plan, LayoutReservation{Kind: ReserveYard, Area: site})
	}
	return plan
}

// topUpYards adds the yards want asks for to plan, sited like topUpHerdSites;
// it reports whether it added any.
func topUpYards(plan LayoutPlan, core []LayoutZone, want int) (LayoutPlan, bool) {
	return topUpSites(plan, core, func(p LayoutPlan) LayoutPlan { return PlanYardSites(p, want) })
}
