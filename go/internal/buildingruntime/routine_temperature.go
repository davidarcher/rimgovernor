package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func NewRoutineTemperaturePlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.temperatureEnabled() {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	if _, ok := native.(observation.TemperatureSource); !ok {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureTemperatureSafety}, nil
}

func (r *RoutineReviewer) temperatureEnabled() bool { return r.methodEnabled(policy.EnsureTemperatureSafety) }

// roomsEnabled reports whether any composed family reads the typed room
// census inside the review bracket: temperature plans need room heat and
// comfort plans need each facility's hosting room role.
func (r *RoutineReviewer) roomsEnabled() bool {
	return r.temperatureEnabled() || r.methodEnabled(policy.EnsureComfort)
}

func (r *RoutineReviewer) methodEnabled(goal policy.GoalID) bool {
	methods, _ := r.methods.Value()
	for _, method := range methods {
		if method == goal {
			return true
		}
	}
	return false
}

func (r *RoutineBuildingPlanner) selectTemperature(facts observation.ColonyProjection, latches policy.RoutineLatches) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	proposal, err := policy.SelectTemperatureMethod(facts.Rooms, r.reviewer.policy, latches)
	if err != nil {
		return nil, "", err
	}
	switch proposal.Method {
	case policy.TemperatureHeat, policy.TemperatureCool:
		resolved := *r
		resolved.temperature = &proposal
		resolved.definition, resolved.environment = string(proposal.Method), policy.PlacementIndoors
		return &resolved, "", nil
	case policy.TemperatureUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.TemperatureNoMethod:
		return nil, BuildingMethodNoDeficit, nil
	default:
		return nil, RoutineBuildingReason(proposal.Method), nil
	}
}

// Completed construction lends bounded ordinary refueling and heat-exchange
// time. Neither the construction receipt nor this allowance establishes safety.
func temperatureOutputAllowance(ctx context.Context, journal *store.Store, goal domain.Goal, current domain.GenerationSnapshot, tick domain.Tick) (uint32, error) {
	methods, err := journal.LoadGoalMethods(ctx, goal.ID, goal.Epoch)
	if err != nil {
		return 0, err
	}
	var allowance uint32
	for _, method := range methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return 0, err
		}
		allowance = max(allowance, temperatureNativeWorkTicks(plan, current, tick))
	}
	return allowance, nil
}

func temperatureNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) != 1 {
		return 0
	}
	progress := plan.Progress[0]
	building, ok := progress.Action().Building()
	if !ok || building.Definition() != "Campfire" && building.Definition() != "PassiveCooler" {
		return 0
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	v := progress.View()
	effect, known := v.Effect.Value()
	if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || tick < v.Tick || tick-v.Tick >= 10000 {
		return 0
	}
	return min(uint32(120), uint32(10000-(tick-v.Tick)))
}
