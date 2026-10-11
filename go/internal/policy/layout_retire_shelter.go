package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Shelter retirement: the temporary shelter leaves the plan once every
// capability it hosts has a better home: sleep (every colonist owns a bed in
// a built bedroom), research (a bench stands in a built room outside the
// shelter, or a planned laboratory stands to take the packed table), craft (a
// planned workshop stands) and heat (on a cold map the bedrooms hold
// temperature). Research is a capability of whichever room holds the bench,
// so a solo colony needs no laboratory. It is re-evaluated every review with
// no latch, so a joiner without a bed brings it back into the gate. A research
// table still standing there is packed by the retired ground's clearance and
// installed from stock.

// ShelterRetirable reports whether every capability the plan's shelter hosts
// has a better home. Unknown or partial facts never retire it, and a colony
// with nothing else built keeps the shelter as its permanent home.
func ShelterRetirable(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, built []CurrentBuilding) bool {
	shelters := plan.roomsOf(PlannedShelter)
	if len(shelters) == 0 || len(sleeping.People) == 0 || len(sleeping.People) != sleeping.Colonists {
		return false
	}
	inShelter := map[string]bool{}
	shelterRoom := map[string]bool{}
	for _, s := range shelters {
		// Census: the shelter's beds are read off its room.
		if room, ok := CensusRoomIn(s, rooms); ok {
			shelterRoom[room.ID] = true
			for _, b := range room.Beds {
				inShelter[b] = true
			}
		}
	}
	bedroomOf := map[string]Room{}
	for _, room := range rooms.Rooms {
		if role, known := room.Role.Value(); known && role == RoomRoleBedroom {
			for _, b := range room.Beds {
				if !inShelter[b] {
					bedroomOf[b] = room
				}
			}
		}
	}
	for _, c := range shelterCapabilities {
		if !capabilityHoused(c, plan, rooms, sleeping, built, shelterRoom, bedroomOf) {
			return false
		}
	}
	return true
}

// capabilityHoused reports a shelter-hosted capability with a standing home
// elsewhere. Cooking has no tested home yet, so it never holds the shelter.
func capabilityHoused(c Capability, plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, built []CurrentBuilding, shelterRoom map[string]bool, bedroomOf map[string]Room) bool {
	switch c {
	case CapSleep:
		for _, p := range sleeping.People {
			if bed, known := p.OwnedBed.Value(); !known || bedroomOf[bed].ID == "" {
				return false
			}
		}
	case CapHeat:
		if !plan.Cold {
			return true
		}
		for _, p := range sleeping.People {
			bed, _ := p.OwnedBed.Value()
			if !positive(bedroomOf[bed].TemperatureControl) {
				return false
			}
		}
	case CapCraft:
		return roleStands(plan, PlannedWorkshop, rooms)
	case CapResearch:
		return roleStands(plan, PlannedLab, rooms) || benchOutside(built, rooms, shelterRoom)
	}
	return true
}

// roleStands reports a planned room of the role, every one standing as a
// roofed census room.
func roleStands(plan LayoutPlan, role PlannedRole, rooms RoomObservation) bool {
	planned := plan.roomsOf(role)
	for _, r := range planned {
		if _, ok := CensusRoomIn(r, rooms); !ok {
			return false
		}
	}
	return len(planned) > 0
}

// benchOutside reports a built research bench standing in an enclosed census
// room other than the shelter's.
func benchOutside(built []CurrentBuilding, rooms RoomObservation, shelterRoom map[string]bool) bool {
	for _, b := range built {
		if !strings.Contains(b.Building.Definition(), "ResearchBench") || len(b.Cells) == 0 {
			continue
		}
		for _, room := range rooms.Rooms {
			if enclosed, known := room.Enclosed.Value(); !known || !enclosed || shelterRoom[room.ID] {
				continue
			}
			if slices.Contains(room.Cells, b.Cells[0]) {
				return true
			}
		}
	}
	return false
}

// InFlightRooms are the interiors of the plan's rooms an open journal plan is
// working on, keyed by origin as InFlightRoomCells is.
func InFlightRooms(plan LayoutPlan, origins map[domain.Cell]bool) map[Rectangle]bool {
	out := map[Rectangle]bool{}
	for _, r := range plan.AllRooms() {
		if origins[domain.Cell{X: r.Region.Bounds().X, Z: r.Region.Bounds().Z}] {
			out[r.Region.Bounds()] = true
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
		if !growth.InFlight[r.Region.Bounds()] {
			drop[r.Region.Bounds()] = true
			ground = append(ground, roomGround(r.Region.Bounds()))
			if r.Airlock != nil {
				ground = append(ground, r.AirlockGround())
			}
			if r.Porch != nil {
				ground = append(ground, r.PorchGround())
			}
		}
	}
	next, dropped := dropRooms(plan, drop)
	if dropped {
		next.RetiredGround = append(slices.Clone(plan.RetiredGround), ground...)
	}
	return next, dropped
}
