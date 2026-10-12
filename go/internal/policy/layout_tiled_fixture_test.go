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

// zonedKitchenPlan is one kitchen over the utility map's zoned core ground:
// the base the pen sites work from, which read the core zones a derived plan
// does not keep.
func zonedKitchenPlan() LayoutPlan {
	kitchen := hallRoom(PlannedKitchen, 100, 100, 6, 5, true)
	return LayoutPlan{Zones: utilityTestZones(), Rooms: []PlannedRoom{kitchen}}.WithRoomIDs()
}

// zonedCoolerPlan is zonedKitchenPlan with a freezer and a tomb, their cooler
// exhausts reserved by reserveExhausts.
func zonedCoolerPlan() LayoutPlan {
	plan := zonedKitchenPlan()
	plan.Rooms = append(plan.Rooms, hallRoom(PlannedFreezer, 108, 100, 5, 5, true), hallRoom(PlannedTomb, 100, 100, 5, 5, false))
	plan = plan.WithRoomIDs()
	reserveExhausts(newUtilityGrid(plan), &plan)
	return plan
}

// exhaustMax is the longest shaft reserveExhausts digs behind a room.
const exhaustMax int32 = 12

// reserveExhausts gives each cooling room that has none a cooler exhaust.
func reserveExhausts(u *utilityGrid, plan *LayoutPlan) {
	for _, role := range coolingRoles {
		for _, r := range plan.Rooms {
			if r.Role != role {
				continue
			}
			if _, _, has := plan.CoolerExhaust(r); has {
				continue
			}
			if area, ok := u.exhaust(r); ok {
				u.reserve(plan, LayoutReservation{Kind: ReserveExhaust, Area: RectRegion(area)})
			}
		}
	}
}

// exhaust is the column behind r's back wall out to the first open cell: one
// cell on an outer face, a dug shaft through rock. False when the back is
// planned or off the map.
func (u *utilityGrid) exhaust(r PlannedRoom) (Rectangle, bool) {
	wall, step, _ := backWall(r)
	first := domain.Cell{X: wall.X + step.X, Z: wall.Z + step.Z}
	c := first
	for n := int32(1); n <= exhaustMax; n++ {
		if !u.in(c.X, c.Z) || u.used[c.Z*u.w+c.X] {
			return Rectangle{}, false
		}
		if !u.rock[c.Z*u.w+c.X] {
			return Rectangle{X: min(first.X, c.X), Z: min(first.Z, c.Z), Width: abs32(c.X-first.X) + 1, Height: abs32(c.Z-first.Z) + 1}, true
		}
		c = domain.Cell{X: c.X + step.X, Z: c.Z + step.Z}
	}
	return Rectangle{}, false
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
