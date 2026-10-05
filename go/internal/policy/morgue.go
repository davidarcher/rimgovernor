package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Staging the morgue (#1820). A fresh stranger corpse the human butchery
// would take (RouteStranger) spoils on the corpse dump's open ground, so the
// planned morgue beside the tomb is shelled once one lies waiting. The
// storage plan then zones it for fresh stranger corpses at Critical
// priority and MaintainRefrigeration cools it like the tomb (WarmTombs).
// Vanilla haulers carry the corpses; nothing here hauls. Colonists keep the
// tomb, and the butcher bill takes corpses from any storage, the morgue's
// included.

// MorgueWaiting reports a fresh stranger corpse lying unburied that the
// human butchery would take.
func MorgueWaiting(waste []WasteItem, butchery bool) bool {
	for _, item := range waste {
		if item.State != WasteBuried && item.CorpseOf == domain.CorpseStranger && RouteStranger(item.RotStage, butchery) == StrangerButcher {
			return true
		}
	}
	return false
}

// MorgueRoomOwed is the planned morgue that stands unbuilt while a fresh
// stranger corpse waits and the butchery is open; false otherwise.
func MorgueRoomOwed(plan LayoutPlan, rooms RoomObservation, waste []WasteItem, butchery bool) (PlannedRoom, bool) {
	if !MorgueWaiting(waste, butchery) {
		return PlannedRoom{}, false
	}
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedMorgue {
			continue
		}
		if _, ok := CensusRoomIn(r, rooms); !ok {
			return r, true
		}
		return PlannedRoom{}, false
	}
	return PlannedRoom{}, false
}
