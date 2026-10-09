package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The planners state the ladder's tiers: the shell ring of each room kind
// takes its role's tier and a loose planner takes its concern's. Paths that
// reach the building funnels through a room (the animal paddock marker is a
// PlannedPen piece) or a concern's planner (excavation and rock steps preview
// the planner's own buildings) take the same rows.
func TestFunnelsStateTheLadderTiers(t *testing.T) {
	for _, c := range []struct {
		name string
		got  domain.ConstructionTier
		want domain.ConstructionTier
	}{
		{"starter shelter ring", policy.RoomTier(policy.PlannedShelter), domain.TierSurvive},
		{"cooking campfire", policy.PlannerTier(policy.EnsureCooking, ""), domain.TierSurvive},
		{"private bedroom", policy.RoomTier(policy.PlannedBedroom), domain.TierSustain},
		{"freezer", policy.PlannerTier(policy.MaintainRefrigeration, ""), domain.TierSustain},
		{"dining room", policy.RoomTier(policy.PlannedDining), domain.TierComfort},
		{"animal paddock marker", policy.RoomTier(policy.PlannedPen), domain.TierComfort},
		{"workshop", policy.RoomTier(policy.PlannedWorkshop), domain.TierProduce},
		{"resource dig and deep drill", policy.PlannerTier(policy.MaintainResource, ""), domain.TierProduce},
		{"graveyard", policy.RoomTier(policy.PlannedGraveyard), domain.TierExpand},
		{"geothermal power dig", policy.PlannerTier(policy.EnsureBasicPower, ""), domain.TierExpand},
		{"killbox rock step", policy.PlannerTier(policy.EnsureDefensiveLayout, ""), domain.TierSecure},
	} {
		if c.got != c.want {
			t.Errorf("%s tier = %d; want %d", c.name, c.got, c.want)
		}
	}
}
