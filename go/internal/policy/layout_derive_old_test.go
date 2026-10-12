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
	want := UtilityWants{TurbinePairs: 1, Solar: 1}
	want.Geysers, want.PenAnimals, want.ThickRoof = footprints, animals, ThickRoofCells(s)
	scorer := newPlanScorer(plan.Zones, plan.Reserved(), s)
	want.scorer = &scorer
	plan = PlanUtilities(plan, want)
	plan = PlanYardSites(plan, YardRoomsWanted(RoomDemand{}))
	plan, _ = growOutskirts(plan, OutskirtsSize())
	plan, _ = growOutskirtsRooms(plan, want.ThickRoof)
	plan = PlanMountainPockets(PlanPerimeter(plan, s), s)
	return domain.Known(withoutCore(plan).WithRoomIDs())
}
