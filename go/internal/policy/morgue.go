package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Staging the morgue (#1820, #2185). The planned morgue stands in the
// outskirts cluster from the start of the plan and is shelled once a human
// corpse, colonist or stranger, fresh or rotten, lies waiting. Cooling never
// gates the shell: a walled-in room already keeps corpses out of sight, and
// cold only slows rot (MaintainRefrigeration cools it like any standing cooling
// room, WarmRooms). Vanilla haulers carry the corpses; nothing here hauls.

// MorgueWaiting reports a human corpse lying unburied.
func MorgueWaiting(waste []WasteItem) bool {
	for _, item := range waste {
		if item.State != WasteBuried && (item.CorpseOf == domain.CorpseStranger || item.CorpseOf == domain.CorpseColonist) {
			return true
		}
	}
	return false
}

// MorgueRoomOwed is the planned morgue whose ring does not match the ground
// while a human corpse waits; false otherwise. The morgue holds no furniture: the build side reconciles its ring
// and floor (ReconcileRoom).
func MorgueRoomOwed(plan LayoutPlan, ground GroundCensus, waste []WasteItem) (PlannedRoom, bool) {
	if !MorgueWaiting(waste) {
		return PlannedRoom{}, false
	}
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedMorgue {
			continue
		}
		if plan.GroundMatches(r, ground) {
			return PlannedRoom{}, false
		}
		return r, true
	}
	return PlannedRoom{}, false
}
