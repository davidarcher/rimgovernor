package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ButcherTableDefinition is the real butcher bench, built from stuff.
const ButcherTableDefinition = "TableButcher"

// cookingDefinitions and butcherDefinitions name the vanilla food-processing
// benches the separation rule reasons about. Butchery generates blood filth
// (and corpse rot) wherever it runs, so a room holding one is inherently
// dirty; a room holding a cooking bench is a workspace whose cleanliness the
// game reads for food poisoning.
var (
	cookingDefinitions = map[Resource]bool{"Campfire": true, "FueledStove": true, "ElectricStove": true}
	butcherDefinitions = map[Resource]bool{ButcherSpotDefinition: true, ButcherTableDefinition: true}
)

func roomHolds(room Room, set map[Resource]bool) bool {
	contents, known := room.Contents.Value()
	if !known {
		return false
	}
	for _, amount := range contents {
		if set[amount.Resource] && amount.Count > 0 {
			return true
		}
	}
	return false
}

// CleanWorkspace reports whether room is one the controller keeps clean:
// an enclosed kitchen, hospital or laboratory, or any enclosed room holding
// a cooking bench (a "Room" role kitchen with a campfire counts). Rooms that
// are inherently dirty -- barns, and any room holding a butcher bench -- are
// never workspaces, whatever else they contain: their filth is the cost of
// the work done there, not a coverage failure.
func CleanWorkspace(room Room) bool {
	enclosed, ek := room.Enclosed.Value()
	if !ek || !enclosed || InherentlyDirty(room) {
		return false
	}
	if role, known := room.Role.Value(); known && (role == RoomRoleKitchen || role == RoomRoleHospital || role == RoomRoleLaboratory) {
		return true
	}
	return roomHolds(room, cookingDefinitions)
}

// InherentlyDirty reports a room whose use produces filth as a matter of
// course (barn, butchery). Cleaning it is ordinary colonist work,
// never a controller intervention.
func InherentlyDirty(room Room) bool {
	if role, known := room.Role.Value(); known && role == RoomRoleBarn {
		return true
	}
	return roomHolds(room, butcherDefinitions)
}

// SeparationRoom is one room holding both a cooking bench and a butcher
// bench: butchery filth lands in the workspace the game reads for food
// poisoning.
type SeparationRoom struct {
	ID    string
	Cells []domain.Cell
}

// KitchenSeparation lists rooms where cooking and butchery share a room,
// with their cells so placement can keep new facilities out. Unknown when
// the room census is unknown.
func KitchenSeparation(rooms domain.Fact[RoomObservation]) domain.Fact[[]SeparationRoom] {
	census, known := rooms.Value()
	if !known {
		return domain.Unknown[[]SeparationRoom]()
	}
	result := []SeparationRoom{}
	for _, room := range census.Rooms {
		if roomHolds(room, cookingDefinitions) && roomHolds(room, butcherDefinitions) {
			result = append(result, SeparationRoom{ID: room.ID, Cells: append([]domain.Cell(nil), room.Cells...)})
		}
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return domain.Known(result)
}

var sleepingDefinitions = map[Resource]bool{"SleepingSpot": true, "DoubleSleepingSpot": true, "Bed": true, "DoubleBed": true, "RoyalBed": true}

// SeparationProtectedCells returns every cell of every room holding the
// benches in set (cooking rooms for a butcher placement, butcher rooms for
// a cooking placement; a butcher also avoids any room the game already
// scores a Kitchen), so a placement search never proposes a site that
// would co-locate the two. Unknown room facts protect nothing: the
// placement's own native preview still owns legality. Sleeping rooms (the
// Bedroom role, or any room holding a bed or sleeping spot, so the starter
// bedroom counts before native scores it) are protected for a butcher
// placement only: blood filth stays out of bedrooms.
func SeparationProtectedCells(rooms domain.Fact[RoomObservation], butcherPlacement bool) []domain.Cell {
	census, known := rooms.Value()
	if !known {
		return nil
	}
	set := butcherDefinitions
	if butcherPlacement {
		set = cookingDefinitions
	}
	var cells []domain.Cell
	for _, room := range census.Rooms {
		role, _ := room.Role.Value()
		if butcherPlacement && (role == RoomRoleBedroom || roomHolds(room, sleepingDefinitions)) || roomHolds(room, set) || butcherPlacement && role == RoomRoleKitchen {
			cells = append(cells, room.Cells...)
		}
	}
	sort.Slice(cells, func(i, j int) bool { return cellLess(cells[i], cells[j]) })
	return cells
}
