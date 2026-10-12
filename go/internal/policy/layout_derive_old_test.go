package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// oldDerive is the fresh plan the old generator made (SiteCore, PlanUtilities):
// the starting plan of the tests of the old replan, which still runs on the
// core grid until it moves onto the chain (#2965). The fresh derive itself is
// DeriveLayoutPlan.
func oldDerive(s MapSurvey, pawns int, tier TechTier, geysers []PowerGeyser, animals int) domain.Fact[LayoutPlan] {
	zones := Zone(s)
	footprints := geyserFootprints(geysers)
	plan := SiteCore(LayoutPlan{Zones: coreWithout(zones, geothermalCells(footprints)), Cold: s.Cold, Hot: s.Hot}, s, pawns, 0, tier)
	if len(plan.AllRooms()) == 0 {
		return domain.Unknown[LayoutPlan]()
	}
	plan.Zones = zones
	plan = PlanUtilities(plan, UtilityWants{Geysers: footprints, PenAnimals: animals})
	plan = PlanYardSites(plan, YardRoomsWanted(RoomDemand{}))
	plan, _ = growOutskirts(plan, OutskirtsSize())
	plan, _ = growOutskirtsRooms(plan, ThickRoofCells(s))
	plan = PlanMountainPockets(PlanPerimeter(plan, s), s)
	return domain.Known(withoutCore(plan).WithRoomIDs())
}
