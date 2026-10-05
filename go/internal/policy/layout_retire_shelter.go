package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Shelter retirement (#2046, epic #2037): the temporary shelter leaves the
// plan once every colonist owns a bed in a built bedroom and the workshop and
// laboratory rooms stand. It is re-evaluated every review with no latch, so a
// joiner without a bed brings it back into the gate. A research table still
// standing there is packed by the retired ground's clearance and installed in
// the laboratory from stock.

// ShelterRetirable reports whether the plan's shelter has done its job: it
// holds a shelter room, every colonist owns a bed in a built bedroom outside
// the shelter, and every planned workshop and laboratory room stands (and there
// is one of each). Unknown or partial facts never retire it.
func ShelterRetirable(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation) bool {
	shelters := plan.roomsOf(PlannedShelter)
	if len(shelters) == 0 || len(sleeping.People) == 0 || len(sleeping.People) != sleeping.Colonists {
		return false
	}
	for _, role := range []PlannedRole{PlannedWorkshop, PlannedLab} {
		planned := plan.roomsOf(role)
		if len(planned) == 0 {
			return false
		}
		for _, r := range planned {
			// Census: a work room must be a roofed room before the shelter retires.
			if _, ok := CensusRoomIn(r, rooms); !ok {
				return false
			}
		}
	}
	inShelter := map[string]bool{}
	for _, s := range shelters {
		// Census: the shelter's beds are read off its room.
		if room, ok := CensusRoomIn(s, rooms); ok {
			for _, b := range room.Beds {
				inShelter[b] = true
			}
		}
	}
	bedroomBed := map[string]bool{}
	for _, room := range rooms.Rooms {
		if role, known := room.Role.Value(); known && role == RoomRoleBedroom {
			for _, b := range room.Beds {
				bedroomBed[b] = !inShelter[b]
			}
		}
	}
	for _, p := range sleeping.People {
		if bed, known := p.OwnedBed.Value(); !known || !bedroomBed[bed] {
			return false
		}
	}
	return true
}

// InFlightRooms are the interiors of the plan's rooms an open journal plan is
// working on, keyed by origin as InFlightRoomCells is.
func InFlightRooms(plan LayoutPlan, origins map[domain.Cell]bool) map[Rectangle]bool {
	out := map[Rectangle]bool{}
	for _, r := range plan.AllRooms() {
		if origins[domain.Cell{X: r.Interior.X, Z: r.Interior.Z}] {
			out[r.Interior] = true
		}
	}
	return out
}

// retireShelter drops the shelter rooms with no open plan working on them
// once growth says the shelter is retirable.
func retireShelter(plan LayoutPlan, growth RoomGrowth) (LayoutPlan, bool) {
	if !growth.RetireShelter {
		return plan, false
	}
	drop := map[Rectangle]bool{}
	var ground []Rectangle
	for _, r := range plan.roomsOf(PlannedShelter) {
		if !growth.InFlight[r.Interior] {
			drop[r.Interior] = true
			ground = append(ground, roomGround(r.Interior))
		}
	}
	next, dropped := dropRooms(plan, drop)
	if dropped {
		next.RetiredGround = append(slices.Clone(plan.RetiredGround), ground...)
	}
	return next, dropped
}
