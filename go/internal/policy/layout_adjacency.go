package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ModuleMealCloset is the dining room's cold meal closet (#936): a 2x2 (or
// 1x2) room behind the dining room's back wall, its only door in that wall,
// cooled by one cooler venting outdoors. The plan adds it only when no
// freezer shares a door with the dining room; the Critical meal stockpile
// then moves into it, where meals never rot.
const ModuleMealCloset ModuleRole = "meal_closet"

// besideRoles pairs a role with the neighbour whose side wall it takes, a
// Link door in the shared wall: the freezer beside the kitchen (#819), so
// the cook steps straight to the shelf, and the dining room beside the
// freezer (#936), so the meal stockpile sits in the cold one door from the
// table, the armory beside the storage room and the wardrobe beside the workshop
// (#1773).
var besideRoles = map[ModuleRole]ModuleRole{ModuleFreezer: ModuleKitchen, ModuleDining: ModuleFreezer, ModuleButchery: ModuleFreezer, ModuleArmory: ModuleStorage, ModuleWardrobe: ModuleWorkshop}

// unlinkedBeside are the besideRoles rooms that share only the wall: the
// armory has no door into the storage room, so haulers never cross its stockpile.
var unlinkedBeside = map[ModuleRole]bool{ModuleArmory: true}

// MealClosetOwed is the planned meal closet with nothing standing on it
// while the planned dining room stands (#936); MaintainRefrigeration shells
// it.
func (p LayoutPlan) MealClosetOwed(rooms RoomObservation) (LayoutRoom, bool) {
	var closet, dining *LayoutRoom
	for i := range p.Rooms {
		switch r := &p.Rooms[i]; {
		case r.Role == ModuleMealCloset && closet == nil:
			closet = r
		case r.Role == ModuleDining && dining == nil:
			dining = r
		}
	}
	if closet == nil || dining == nil {
		return LayoutRoom{}, false
	}
	if _, ok := PlannedRoomStanding(*dining, rooms); !ok {
		return LayoutRoom{}, false
	}
	if _, ok := PlannedRoomStanding(*closet, rooms); ok {
		return LayoutRoom{}, false
	}
	return *closet, true
}

// FreezerOpensInto reports a planned freezer sharing a door with dining:
// a Link of either room in the wall between them (#936).
func (p LayoutPlan) FreezerOpensInto(dining LayoutRoom) bool {
	_, _, ok := p.FreezerDoorInto(dining)
	return ok
}

// FreezerDoorInto is the first planned freezer sharing a door with dining,
// and that door.
func (p LayoutPlan) FreezerDoorInto(dining LayoutRoom) (LayoutRoom, domain.Cell, bool) {
	for _, f := range p.Rooms {
		if f.Role != ModuleFreezer {
			continue
		}
		for _, link := range []*domain.Cell{f.Link, dining.Link} {
			if link != nil && inWall(f.Interior, *link) && inWall(dining.Interior, *link) {
				return f, *link, true
			}
		}
	}
	return LayoutRoom{}, domain.Cell{}, false
}
