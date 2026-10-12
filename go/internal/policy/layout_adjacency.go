package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// PlannedMealCloset is the dining room's cold meal closet: a 2x2 (or
// 1x2) room behind the dining room's back wall, its only door in that wall,
// cooled by one cooler venting outdoors. The plan adds it only when no
// freezer shares a door with the dining room; the Critical meal stockpile
// then moves into it, where meals never rot.
const PlannedMealCloset PlannedRole = "meal_closet"

// MealClosetOwed is the planned meal closet with nothing standing on it
// while the planned dining room stands; MaintainRefrigeration shells
// it.
func (p LayoutPlan) MealClosetOwed(rooms RoomObservation) (PlannedRoom, bool) {
	var closet, dining *PlannedRoom
	for i := range p.Rooms {
		switch r := &p.Rooms[i]; {
		case r.Role == PlannedMealCloset && closet == nil:
			closet = r
		case r.Role == PlannedDining && dining == nil:
			dining = r
		}
	}
	if closet == nil || dining == nil {
		return PlannedRoom{}, false
	}
	// Census: the closet shells only once dining is a usable room.
	if _, ok := CensusRoomIn(*dining, rooms); !ok {
		return PlannedRoom{}, false
	}
	// Census: an unroofed closet ring is still owed its shell.
	if _, ok := CensusRoomIn(*closet, rooms); ok {
		return PlannedRoom{}, false
	}
	return *closet, true
}

// FreezerOpensInto reports a planned freezer sharing a door with dining:
// a Link of either room in the wall between them.
func (p LayoutPlan) FreezerOpensInto(dining PlannedRoom) bool {
	_, _, ok := p.FreezerDoorInto(dining)
	return ok
}

// FreezerDoorInto is the first planned freezer sharing a door with dining,
// and that door.
func (p LayoutPlan) FreezerDoorInto(dining PlannedRoom) (PlannedRoom, domain.Cell, bool) {
	for _, f := range p.Rooms {
		if f.Role != PlannedFreezer {
			continue
		}
		for _, link := range append(p.LinksOf(f), p.LinksOf(dining)...) {
			if inWall(f.Region.Bounds(), link) && inWall(dining.Region.Bounds(), link) {
				return f, link, true
			}
		}
	}
	return PlannedRoom{}, domain.Cell{}, false
}
