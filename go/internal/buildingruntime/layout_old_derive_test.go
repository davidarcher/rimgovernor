package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// oldDerive is the fresh plan the old generator made (a core grid with its
// hallways, a yard, the perimeter; no power sites): the plan
// these tests read the old shapes of (the courtyard, the ring's stretches)
// from. The runtime derives from the station chain (policy.DeriveLayoutPlan);
// this goes with the generator (#2932).
func oldDerive(s policy.MapSurvey, pawns int, tier policy.TechTier, animals int) domain.Fact[policy.LayoutPlan] {
	zones := policy.Zone(s)
	plan := policy.SiteCore(policy.LayoutPlan{Zones: zones, Cold: s.Cold, Hot: s.Hot}, s, pawns, 0, tier)
	if len(plan.AllRooms()) == 0 {
		return domain.Unknown[policy.LayoutPlan]()
	}
	plan = policy.PlanUtilities(plan, policy.UtilityWants{PenAnimals: animals})
	plan = policy.PlanYardSites(plan, policy.YardRoomsWanted(policy.RoomDemand{}))
	plan = policy.PlanMountainPockets(policy.PlanPerimeter(plan, s), s)
	var kept []policy.LayoutZone
	for _, z := range plan.Zones {
		if z.Kind != policy.ZoneCore {
			kept = append(kept, z)
		}
	}
	plan.Zones = kept
	return domain.Known(plan.WithRoomIDs())
}
