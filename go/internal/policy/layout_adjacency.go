package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// PlannedMealCloset is the dining room's cold meal closet (#936): a 2x2 (or
// 1x2) room behind the dining room's back wall, its only door in that wall,
// cooled by one cooler venting outdoors. The plan adds it only when no
// freezer shares a door with the dining room; the Critical meal stockpile
// then moves into it, where meals never rot.
const PlannedMealCloset PlannedRole = "meal_closet"

// besideRoles pairs a role with the neighbour whose side wall it takes, a
// Link door in the shared wall: the freezer beside the kitchen (#819), so
// the cook steps straight to the shelf, and the dining room beside the
// freezer (#936), so the meal stockpile sits in the cold one door from the
// table, the armory beside the storage room and the wardrobe beside the workshop
// (#1773).
var besideRoles = map[PlannedRole]relationSpec{
	PlannedFreezer:  {neighbour: PlannedKitchen},
	PlannedDining:   {neighbour: PlannedFreezer},
	PlannedButchery: {neighbour: PlannedFreezer, backFirst: true, endCell: true},
	PlannedArmory:   {neighbour: PlannedStorage, backLast: true, unlinked: true},
	PlannedWardrobe: {neighbour: PlannedWorkshop, backLast: true},
}

// relationSpec places a role against its neighbour's side walls (east, then
// west), each with a Link door unless unlinked. The back wall, the one
// opposite the hallway, is tried before the sides when backFirst and after
// them when backLast. The armory is unlinked: it has no door into the
// storage room, so haulers never cross its stockpile.
type relationSpec struct {
	neighbour PlannedRole
	backFirst bool
	backLast  bool
	unlinked  bool
	// endCell: the back-wall room overlaps only an end cell of the
	// neighbour, whose cooler vents out of the middle of that wall.
	endCell bool
}

// besideOf is the neighbour role takes its side against, if it has one.
func besideOf(role PlannedRole) (PlannedRole, bool) {
	rule, ok := besideRoles[role]
	return rule.neighbour, ok
}

// MealClosetOwed is the planned meal closet with nothing standing on it
// while the planned dining room stands (#936); MaintainRefrigeration shells
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
	if _, ok := CensusRoomIn(*dining, rooms); !ok {
		return PlannedRoom{}, false
	}
	if _, ok := CensusRoomIn(*closet, rooms); ok {
		return PlannedRoom{}, false
	}
	return *closet, true
}

// FreezerOpensInto reports a planned freezer sharing a door with dining:
// a Link of either room in the wall between them (#936).
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
		for _, link := range []*domain.Cell{f.Link, dining.Link} {
			if link != nil && inWall(f.Interior, *link) && inWall(dining.Interior, *link) {
				return f, *link, true
			}
		}
	}
	return PlannedRoom{}, domain.Cell{}, false
}
