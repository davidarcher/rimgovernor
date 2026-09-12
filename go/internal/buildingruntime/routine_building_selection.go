package buildingruntime

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const BuildingExistingFacility RoutineBuildingReason = "existing_facility_needs_bill_or_upkeep"

func pendingCampfire(progress domain.Progress) bool {
	return pendingFacility(progress, "Campfire")
}

func pendingFacility(progress domain.Progress, definition string) bool {
	building, ok := progress.Action().Building()
	return ok && building.Definition() == definition && domain.GoalWorkOpen([]domain.Progress{progress})
}

func NewRoutineCookingPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureCooking, definition: "Campfire"}, nil
}

func (r *RoutineBuildingPlanner) selection(facts observation.ColonyProjection) (int64, domain.MethodID, RoutineBuildingReason) {
	count, known := facts.Facts.Colonists.Value()
	if !known || count <= 0 {
		return 0, "", BuildingMethodUnknown
	}
	switch r.goal {
	case policy.EnsureComfort:
		return 1, domain.MethodID("comfort-" + r.definition), ""
	case policy.EnsureInitialShelter, policy.EnsureExpansion:
		capacity, known := facts.Facts.IndoorCapacity.Value()
		if !known {
			return 0, "", BuildingMethodUnknown
		}
		if r.goal == policy.EnsureExpansion {
			if count >= 1<<63-1 {
				return 0, "", BuildingMethodUnknown
			}
			count++
		} else if target, known := facts.Facts.HousingTarget.Value(); known {
			count = max(count, target)
		}
		missing := count - capacity
		if missing <= 0 {
			return 0, "", BuildingMethodNoDeficit
		}
		if missing > 64 {
			return 0, "", BuildingMethodNoSpace
		}
		if r.shelter {
			return 32, "starter-shell", ""
		}
		return missing, domain.MethodID(fmt.Sprintf("indoor-sleeping-%d-%d", count, missing)), ""
	case policy.EnsureCooking:
		ready, known := facts.Facts.Cooking.Value()
		if !known {
			return 0, "", BuildingMethodUnknown
		}
		if ready {
			return 0, "", BuildingMethodNoDeficit
		}
		benches, known := facts.CookingBenches.Value()
		if !known {
			return 0, "", BuildingMethodUnknown
		}
		unknown := false
		for _, bench := range benches {
			usable, known := bench.Usable.Value()
			if bench.Definition == "Campfire" || known && usable {
				return 0, "", BuildingExistingFacility
			}
			unknown = unknown || !known
		}
		if unknown {
			return 0, "", BuildingMethodUnknown
		}
		return 1, "campfire", ""
	default:
		return 0, "", BuildingMethodUnknown
	}
}
