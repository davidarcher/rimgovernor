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

func (r *RoutineReviewer) temperatureEnabled() bool {
	return r.methodEnabled(policy.EnsureTemperatureSafety)
}

// roomsEnabled reports whether any composed family reads the typed room
// census inside the review bracket: temperature and refrigeration plans need
// room heat, comfort plans need each facility's hosting room role and
// cleaning plans need each room's measured cleanliness.
func (r *RoutineReviewer) roomsEnabled() bool {
	return r.temperatureEnabled() || r.methodEnabled(policy.EnsureComfort) || r.methodEnabled(policy.MaintainRefrigeration) || r.methodEnabled(policy.MaintainCleanFacilities) || r.methodEnabled(policy.MaintainLighting) || r.methodEnabled(policy.MaintainFlooring) || r.methodEnabled(policy.MaintainRoutes)
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
	cooling := temperatureCooling(facts)
	proposal, err := policy.SelectTemperatureMethod(facts.Rooms, cooling, r.reviewer.policy, latches)
	if err != nil {
		return nil, "", err
	}
	if clockDebug() && (latches.Hot || proposal.Method == policy.TemperatureCoolPowered) {
		spare := domain.Unknown[float64]()
		if topology, known := cooling.Power.Value(); known {
			spare = topology.SpareW()
		}
		clockSchedulerLog("temperature: proposal=%s room=%s coolerAvailable=%+v draw=%+v spare=%+v cells=%d", proposal.Method, proposal.Room, cooling.CoolerAvailable, cooling.CoolerDrawW, spare, len(cooling.Cells))
	}
	switch proposal.Method {
	case policy.TemperatureHeat, policy.TemperatureCool, policy.TemperatureCoolPowered:
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

// temperatureCooling assembles the powered cooler evidence from the rooms
// reading: the Cooler planning definition (availability, draw), the colony
// power topology and the site cells the vented-wall search walks.
func temperatureCooling(facts observation.ColonyProjection) policy.TemperatureCooling {
	cooling := policy.TemperatureCooling{CoolerAvailable: domain.Unknown[bool](), CoolerDrawW: domain.Unknown[float64](), Power: facts.PowerPlanning, Cells: facts.Cells}
	for _, d := range facts.Definitions {
		if d.Name == "Cooler" {
			cooling.CoolerAvailable, cooling.CoolerDrawW = d.Available, d.PowerW
		}
	}
	return cooling
}

// temperatureDefinitions are the planning definitions the temperature
// planner reads: every method it can place, so availability and draw are
// known before one is chosen.
var temperatureDefinitions = []string{"Campfire", "PassiveCooler", "Cooler"}

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
	if !ok || building.Definition() != "Campfire" && building.Definition() != "PassiveCooler" && building.Definition() != "Cooler" {
		return 0
	}
	// Native order-generation drift (authority reacquired since the build)
	// does not unbuild the structure: the receipt must match the world and
	// the plan revision it served, not the native generation.
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	v := progress.View()
	current.Native = v.Snapshot.Native
	effect, known := v.Effect.Value()
	if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || tick < v.Tick || tick-v.Tick >= 10000 {
		return 0
	}
	return min(uint32(120), uint32(10000-(tick-v.Tick)))
}
