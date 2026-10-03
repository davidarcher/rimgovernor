package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The creepjoiner isolation room (#1740, epic #1694): one planned room
// holding one bed, owed while a creepjoiner whose downside has not shown and
// whose inspection the colony has not finished lives in the colony. It is
// staged like the containment cell (containment_cell.go): the layout review
// grows a core room sized to the bed, MaintainHousing shells it and places
// the bed. The bed is the definition catalog's own (a humanlike sleeping bed
// the ThingDef mirror's bed_humanlike field names, the fewest-slots one the
// colony would sleep in), never a name here. The room is a plan role only:
// the game scores it a bedroom, which is what its furnishing makes it.
//
// A creepjoiner is held in the room by restricting its allowed area to the
// bot-owned Isolation area (IsolationAreaKey), once the room stands with its
// bed; ManageCreepJoiners releases the restriction when the downside shows,
// when the colony's record says the inspection is done, or while the pawn is
// hungry (a pawn restricted to an area treats food outside it as forbidden,
// decompile: ForbidUtility.InAllowedArea).

// ModuleIsolationRoom is the isolation room's plan role.
const ModuleIsolationRoom ModuleRole = "isolation-room"

// IsolationAreaKey is the bot area key of the Isolation allowed area: the
// plan's isolation room interior.
const IsolationAreaKey = "Isolation"

// IsolationAreaLabel is the native label of the Isolation area.
const IsolationAreaLabel = IsolationAreaKey

// IsolationPlanning is the projection's isolation inputs: the creepjoiners
// the colony holds apart and the bed definitions the room can be furnished
// from. Pawns is unknown until the pawn rows are read (and without Anomaly).
type IsolationPlanning struct {
	Pawns domain.Fact[[]PawnID]
	Beds  []string
}

// IsolationRoomNeed is the room the isolated creepjoiners owe: one bed in
// its own room (one bed however many creepjoiners; they are rare). No
// isolated creepjoiner, an unread census, or a bed that is not buildable
// yet or has no known size owes nothing.
func IsolationRoomNeed(p IsolationPlanning, furniture []FurnitureDefinition) (ChildRoomNeed, bool) {
	pawns, known := p.Pawns.Value()
	if !known || len(pawns) == 0 || len(p.Beds) == 0 {
		return ChildRoomNeed{}, false
	}
	need := ChildRoomNeed{Role: RoomRoleIsolationRoom, Module: ModuleIsolationRoom, Furniture: []ChildFurniture{{Defs: p.Beds, Count: 1}}}
	if _, ok := need.resolve(furniture); !ok {
		return ChildRoomNeed{}, false
	}
	return need, true
}

// IsolationRoomStanding reports whether the plan's isolation room for need
// stands with every piece of its furniture: the room is enclosed in the
// census and the bed is built inside it. Only then is a creepjoiner held in
// it.
func IsolationRoomStanding(plan LayoutPlan, rooms RoomObservation, built []CurrentBuilding, need ChildRoomNeed, defs []FurnitureDefinition) bool {
	shape, ok := need.shape(defs)
	if !ok {
		return false
	}
	room, ok := plan.ChildRoomFor(shape)
	if !ok {
		return false
	}
	if _, standing := PlannedRoomStanding(room, rooms); !standing {
		return false
	}
	pieces, _ := need.resolve(defs)
	for _, p := range pieces {
		if standingChildPieces(room, p.Defs, built) < p.Count {
			return false
		}
	}
	return true
}

// IsolationRoomCells are the interior cells of the plan's isolation rooms,
// sorted.
func (p LayoutPlan) IsolationRoomCells() []domain.Cell {
	set := map[domain.Cell]bool{}
	for _, room := range p.AllRooms() {
		if room.Role != ModuleIsolationRoom {
			continue
		}
		for _, c := range rectCells(room.Interior) {
			set[c] = true
		}
	}
	return sortedCells(set)
}
