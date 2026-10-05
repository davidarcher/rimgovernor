package policy

import "errors"

// Demand-grown core rooms. A colony starts with only coreBaseRooms and the
// bedroom wings; every room below joins the plan once its need shows, at the
// nearest core slot like any other add-on (SiteRoom), beside its anchor when
// besideRoles names one (the butchery behind the freezer). Nothing is
// reserved ahead of the need, so no rock is dug and no ground held for a room
// that is not up for building. The tomb is grown through the replan's tomb
// count instead (RoomGrowth is not asked for it).

// demandCoreRooms are the rooms RoomGrowth.Core may ask for, in the order
// they are added.
var demandCoreRooms = []PlannedRole{
	PlannedHospital, PlannedLab, PlannedRec, PlannedButchery, PlannedPrison, PlannedMorgue, PlannedBattery,
}

// CoreRoomsOwed is the rooms of wanted that plan lacks, in demandCoreRooms
// order. A role outside demandCoreRooms is ignored.
func CoreRoomsOwed(plan LayoutPlan, wanted []PlannedRole) []PlannedRole {
	want := map[PlannedRole]bool{}
	for _, r := range wanted {
		want[r] = true
	}
	have := map[PlannedRole]bool{}
	for _, r := range plan.AllRooms() {
		have[r.Role] = true
	}
	var owed []PlannedRole
	for _, role := range demandCoreRooms {
		if want[role] && !have[role] {
			owed = append(owed, role)
		}
	}
	return owed
}

// growDemandRooms adds each room of wanted that plan lacks and gives a newly
// cooled room its cooler exhaust. It reports whether a room was added and the
// rooms it could not place.
func growDemandRooms(plan LayoutPlan, s MapSurvey, sc *planScorer, wanted []PlannedRole) (LayoutPlan, bool, error) {
	if len(plan.Hallways()) == 0 {
		return plan, false, nil
	}
	grew := false
	var unplaced []error
	for _, role := range CoreRoomsOwed(plan, wanted) {
		var added bool
		var err error
		if plan, added, err = SiteRoom(plan, sc, role, coreRoomSize[role]); added {
			grew = true
		} else if err != nil {
			unplaced = append(unplaced, err)
		}
	}
	if grew {
		u := newUtilityGrid(plan)
		u.thick = ThickRoofCells(s)
		reserveExhausts(u, &plan)
	}
	return plan, grew, errors.Join(unplaced...)
}

// WithCoreRooms is plan with the demand rooms of wanted grown on, for a
// caller that audits a room the start plan no longer holds. It reports the
// rooms it could not place.
func WithCoreRooms(plan LayoutPlan, wanted ...PlannedRole) (LayoutPlan, error) {
	grown, _, err := growDemandRooms(plan, MapSurvey{}, nil, wanted)
	return grown, err
}
