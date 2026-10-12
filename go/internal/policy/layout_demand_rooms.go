package policy

import (
	"slices"
)

// Demand-grown core rooms. A colony starts with only the base rooms (defaultOwedRooms) and the
// bedroom wings; every room below joins the plan once its need shows, at the
// nearest core slot like any other add-on, beside its anchor when
// besideRoles names one (the butchery behind the freezer). Nothing is
// reserved ahead of the need, so no rock is dug and no ground held for a room
// that is not up for building. The tomb and the morgue are planned from the start in the outskirts cluster
// (layout_outskirts_cluster.go); a further tomb is grown through the replan's
// tomb count (RoomGrowth is not asked for it).

// demandCoreRooms are the rooms RoomGrowth.Core may ask for, in the order
// they are added.
var demandCoreRooms = []PlannedRole{
	PlannedHospital, PlannedLab, PlannedRec, PlannedButchery, PlannedPrison, PlannedBattery,
}

// designatedHosts are the rooms that serve a capability by template rather
// than by catalog compatibility: a workshop could take a research bench, but
// only the shelter is planned to.
var designatedHosts = map[PlannedRole][]Capability{PlannedShelter: shelterCapabilities}

// HostsCapability reports a planned room other than except that is designated to
// host c.
func (p LayoutPlan) HostsCapability(c Capability, except PlannedRole) bool {
	for _, r := range p.Rooms {
		if r.Role != except && slices.Contains(designatedHosts[r.Role], c) {
			return true
		}
	}
	return false
}
