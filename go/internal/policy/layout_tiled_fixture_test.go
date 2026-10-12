package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Fixtures that start a test from the plan DeriveLayoutPlan lays; the old
// generator's core plans are gone.

// tiledPlan is the fresh plan DeriveLayoutPlan lays over s for pawns
// colonists, or a plan holding only s's zones when it lays none.
func tiledPlan(s MapSurvey, pawns int) LayoutPlan {
	if plan, ok := derivePlan(s, pawns, nil); ok {
		return plan
	}
	return LayoutPlan{Zones: Zone(s)}
}

// derivePlan is DeriveLayoutPlan over s for pawns colonists with the geysers
// reported and 30 pen animals; false when it lays no plan.
func derivePlan(s MapSurvey, pawns int, geysers []PowerGeyser) (LayoutPlan, bool) {
	fact, _, err := DeriveLayoutPlan(s, colonyStationFacts(pawns, s), geysers, 30)
	plan, ok := fact.Value()
	return plan, ok && err == nil
}

// zonedKitchenPlan is one kitchen on a hallway over the utility map's zoned
// core ground: the base PlanUtilities and the pen sites work from, which read
// the hallway and the core zones a derived plan does not keep.
func zonedKitchenPlan() LayoutPlan {
	kitchen := hallRoom(PlannedKitchen, 100, 100, 6, 5, true)
	hall := SpineSegment{From: domain.Cell{X: 70, Z: 100}, To: domain.Cell{X: 130, Z: 100}}
	return LayoutPlan{Zones: utilityTestZones(), Spine: []SpineSegment{hall}, Rooms: []PlannedRoom{kitchen}}.WithRoomIDs()
}

// zonedCoolerPlan is zonedKitchenPlan with a freezer and a tomb on the hallway,
// their cooler exhausts reserved: PlanUtilities is the only writer of those
// reservations.
func zonedCoolerPlan() LayoutPlan {
	plan := zonedKitchenPlan()
	plan.Rooms = append(plan.Rooms, hallRoom(PlannedFreezer, 108, 100, 5, 5, true), hallRoom(PlannedTomb, 100, 100, 5, 5, false))
	return PlanUtilities(plan.WithRoomIDs(), UtilityWants{})
}

// coldTiledPlan is the plan for three colonists on a cold or temperate map.
func coldTiledPlan(cold bool) LayoutPlan {
	s := coreTestSurvey()
	s.Cold = cold
	return tiledPlan(s, 3)
}

// shelterHolds reports whether the shelter's interior is at least the area the
// colony, campfires and coolers need, in either orientation: the tiler picks
// the shape, the sizing owes the area.
func shelterHolds(room PlannedRoom, colonists, campfires, coolers int) bool {
	b := room.Region.Bounds()
	return int(b.Width)*int(b.Height) >= ShelterInteriorArea(colonists, campfires, coolers)
}

// utilityTestZones is utilityTestSurvey's zones: a map wide enough for utility
// sites beyond the plan ring's keep-out.
func utilityTestZones() []LayoutZone { return Zone(utilityTestSurvey()) }

// coreTestZones is coreTestSurvey's zones.
func coreTestZones() []LayoutZone { return Zone(coreTestSurvey()) }

// coreTestSurvey is soil everywhere with a rock block in the east third.
func coreTestSurvey() MapSurvey {
	return zoningSurvey(120, func(x, z int32) SurveyCell {
		if x >= 80 {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
}

// utilityTestSurvey is soil with a rock block from x = 180 on a 220 map.
func utilityTestSurvey() MapSurvey {
	return zoningSurvey(220, func(x, z int32) SurveyCell {
		if x >= 180 {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
}
