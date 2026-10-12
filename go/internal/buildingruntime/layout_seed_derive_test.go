package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ringedPlan is a small hand-placed core (a shelter, a workshop and a kitchen
// in a row at the middle of s) with the perimeter laid over it: the ring, its
// gates and the killbox these tests cut and replan. Unknown when no ring fits.
// The runtime derives its plans from the station chain
// (policy.DeriveLayoutPlan); the tests here read only the ring.
func ringedPlan(s policy.MapSurvey) domain.Fact[policy.LayoutPlan] {
	room := func(role policy.PlannedRole, x, z, w, h int32) policy.PlannedRoom {
		return policy.PlannedRoom{Role: role, Region: policy.RectRegion(policy.Rectangle{X: x, Z: z, Width: w, Height: h}), Door: domain.Cell{X: x + w/2, Z: z + h + 1}, DoorRot: domain.South}
	}
	cx, cz := s.Bounds.Width/2-12, s.Bounds.Height/2-3
	plan := policy.LayoutPlan{
		Zones: policy.Zone(s),
		Rooms: []policy.PlannedRoom{
			room(policy.PlannedShelter, cx, cz, 6, 5),
			room(policy.PlannedWorkshop, cx+9, cz, 6, 5),
			room(policy.PlannedKitchen, cx+18, cz, 6, 5),
		},
	}
	plan = policy.PlanPerimeter(plan.WithRoomIDs(), s)
	if _, ring := plan.Ring(); !ring {
		return domain.Unknown[policy.LayoutPlan]()
	}
	return domain.Known(plan.WithRoomIDs())
}
