package buildingruntime

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const BuildingExistingFacility RoutineBuildingReason = "existing_facility_needs_bill_or_upkeep"

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

func NewRoutineButcherPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureFoodSupply, definition: "ButcherSpot", environment: policy.PlacementAnywhere}, nil
}
func (r *RoutineBuildingPlanner) selection(facts observation.ColonyProjection) (int64, domain.MethodID, RoutineBuildingReason) {
	count, known := facts.Facts.Colonists.Value()
	if !known || count <= 0 {
		return 0, "", BuildingMethodUnknown
	}
	switch r.goal {
	case policy.EnsureFoodSupply:
		if r.definition != "ButcherSpot" {
			return 0, "", BuildingMethodUnknown
		}
		days, dk := facts.Facts.FoodDays.Value()
		armed, ak := facts.Facts.Armed.Value()
		if !dk || !ak || armed <= 0 || days >= r.reviewer.policy.FoodTargetDays {
			return 0, "", BuildingMethodNoDeficit
		}
		benches, bk := facts.ButcheringBenches.Value()
		if !bk {
			return 0, "", BuildingMethodUnknown
		}
		if len(benches) > 0 {
			// A butcher bench that shares a room with a cooking bench keeps
			// the colony fed but not clean (issue #6 slice 2): when every
			// bench is co-located and the census can say so, a separate
			// butcher spot is admitted outside. Deconstructing the shared
			// one needs a generic deconstruct action (follow-up).
			if !butchersAllColocated(benches, facts.Rooms) {
				return 0, "", BuildingExistingFacility
			}
			return 1, "butcher-spot-separated", ""
		}
		return 1, "butcher-spot", ""

	case policy.EnsureTemperatureSafety:
		if r.temperature == nil {
			return 0, "", BuildingMethodUnknown
		}
		return 1, r.temperature.Key, ""
	case policy.MaintainRefrigeration:
		if r.refrigeration == nil || r.refrigeration.Method != policy.RefrigerationBuild {
			return 0, "", BuildingMethodUnknown
		}
		return 1, r.refrigeration.Key, ""
	case policy.MaintainLighting:
		if r.lighting == nil || r.lighting.Method != policy.LightingBuild {
			return 0, "", BuildingMethodUnknown
		}
		return 1, r.lighting.Key, ""
	case policy.MaintainFlooring:
		if r.flooring == nil || r.flooring.Method != policy.FlooringBuild {
			return 0, "", BuildingMethodUnknown
		}
		return int64(len(r.flooring.Cells)), r.flooring.Key, ""
	case policy.MaintainRoutes:
		if r.routes == nil || r.routes.Method != policy.RoutesBuild {
			return 0, "", BuildingMethodUnknown
		}
		return 1, r.routes.Key, ""
	case policy.EnsureBasicPower:
		if r.power == nil {
			return 0, "", BuildingMethodUnknown
		}
		if r.power.Method == policy.PowerConnect {
			return int64(len(r.power.Cells)), r.power.Key, ""
		}
		if r.power.Method == policy.PowerGenerate {
			return 1, r.power.Key, ""
		}
		return 0, "", BuildingMethodUnknown
	case policy.EnsureComfort:
		if r.shelter {
			return 32, "comfort-shell", ""
		}
		return 1, domain.MethodID("comfort-" + r.definition), ""
	case policy.MaintainResource:
		if r.shelter {
			return 32, "workshop-shell", ""
		}
		return 1, domain.MethodID("workshop-" + r.definition), ""
	case policy.MaintainMedicalCare:
		if r.shelter {
			return 32, "hospital-shell", ""
		}
		return 1, domain.MethodID("hospital-" + r.definition), ""
	case policy.MaintainSleeping:
		if r.shelter {
			return 32, "sleeping-shell", ""
		}
		// One method per bed still owed: the count falls once a staged bed
		// is assigned, so the next bed is a new method in the same epoch.
		if r.sleeping == nil {
			return 0, "", BuildingMethodUnknown
		}
		return 1, domain.MethodID(fmt.Sprintf("sleeping-%s-%d", r.definition, r.sleeping.Unhoused)), ""
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

// butchersAllColocated is true only when the room census is known and every
// butcher bench stands in a room that also holds a cooking bench. An unknown
// room keeps the bench counted as a facility.
func butchersAllColocated(benches []observation.CookingBench, rooms domain.Fact[policy.RoomObservation]) bool {
	shared, known := policy.KitchenSeparation(rooms).Value()
	if !known || len(benches) == 0 {
		return false
	}
	colocated := map[string]bool{}
	for _, room := range shared {
		colocated[room.ID] = true
	}
	for _, bench := range benches {
		room, known := bench.Room.Value()
		if !known || !colocated[room] {
			return false
		}
	}
	return true
}
