package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const BuildingComfortWait RoutineBuildingReason = "waiting_for_native_comfort_use"

func NewRoutineComfortPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureComfort}, nil
}

func (r *RoutineBuildingPlanner) selectComfort(facts observation.ColonyProjection, history policy.ComfortHistory) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	v, known := facts.Facts.Comfort.Value()
	if !known {
		return nil, BuildingMethodUnknown, nil
	}
	review, err := policy.ReviewComfort(facts.Facts.Comfort, history, facts.Identity.Tick)
	if err != nil {
		return nil, "", err
	}
	method, err := policy.SelectComfortMethod(v, review)
	if err != nil {
		return nil, "", err
	}
	switch method {
	case policy.ComfortNoMethod:
		return nil, BuildingMethodNoDeficit, nil
	case policy.ComfortWait:
		return nil, BuildingComfortWait, nil
	case policy.ComfortAccessBlocked:
		return nil, BuildingExistingFacility, nil
	}
	resolved := *r
	resolved.definition = string(method)
	resolved.environment = policy.PlacementIndoors
	if method == policy.ComfortBuildRecreation {
		resolved.environment = policy.PlacementAnywhere
	}
	if method == policy.ComfortBuildChair {
		for _, s := range v.Surfaces {
			resolved.adjacent = append(resolved.adjacent, s.Adjacent...)
		}
	}
	for _, d := range facts.Definitions {
		if d.Name == resolved.definition {
			if stuff, known := d.Stuff.Value(); known {
				resolved.stuff = stuff
			}
		}
	}
	return &resolved, "", nil
}

// Allow a finite interval for ordinary dining/recreation after this direction
// completed a comfort facility. Repeated observations cannot renew the budget.
func comfortNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) != 1 {
		return 0
	}
	p := plan.Progress[0]
	b, ok := p.Action().Building()
	if !ok || b.Definition() != "Table1x2c" && b.Definition() != "DiningChair" && b.Definition() != "HorseshoesPin" {
		return 0
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	v := p.View()
	effect, known := v.Effect.Value()
	if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || tick < v.Tick || tick-v.Tick >= 10000 {
		return 0
	}
	return uint32(10000 - (tick - v.Tick))
}
