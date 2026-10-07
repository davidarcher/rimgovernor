package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func NewRoundsTemperaturePlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.temperatureEnabled() {
		return nil, fmt.Errorf("%w: NewRoundsTemperaturePlanner: reviewer == nil || native == nil || !reviewer.temperatureEnabled()", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsTemperaturePlanner: !ok", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.EnsureTemperatureSafety}, nil
}

func (r *Rounder) temperatureEnabled() bool {
	return r.methodEnabled(policy.EnsureTemperatureSafety)
}

// roomsEnabled reports whether any composed family reads the typed room
// census inside the review bracket: temperature and refrigeration plans need
// room heat, comfort plans need each facility's hosting room role and
// cleaning plans need each room's measured cleanliness.
func (r *Rounder) roomsEnabled() bool {
	// MaintainHousing: suite claims and the plan's suite wing read the
	// census's standing bedrooms (#1221). MaintainShelter: the Safe area
	// covers the census's enclosed roofed rooms (#1325).
	return r.temperatureEnabled() || r.methodEnabled(policy.MaintainShelter) || r.methodEnabled(policy.MaintainHousing) || r.methodEnabled(policy.EnsureComfort) || r.methodEnabled(policy.MaintainRefrigeration) || r.methodEnabled(policy.MaintainCleanFacilities) || r.methodEnabled(policy.MaintainLighting) || r.methodEnabled(policy.MaintainFlooring) || r.methodEnabled(policy.MaintainRoutes) || r.methodEnabled(policy.MaintainBurial) || r.methodEnabled(policy.MaintainIncineration) || r.methodEnabled(policy.MaintainStockpiles)
}

func (r *Rounder) methodEnabled(goal policy.ConcernID) bool {
	methods, _ := r.methods.Value()
	for _, method := range methods {
		if method == goal {
			return true
		}
	}
	return false
}

func (r *RoundsBuildingPlanner) selectTemperature(facts observation.ColonyProjection, latches policy.RoundsLatches) (*RoundsBuildingPlanner, Verdict, error) {
	cooling := temperatureCooling(facts)
	proposal, err := policy.SelectTemperatureMethod(facts.Rooms, cooling, r.reviewer.policy, latches)
	if err != nil {
		return nil, Verdict{}, err
	}
	switch proposal.Method {
	case policy.TemperatureHeat, policy.TemperatureCool, policy.TemperatureCoolPowered, policy.TemperatureRefuelOff, policy.TemperatureRefuelOn:
		resolved := *r
		resolved.temperature = &proposal
		resolved.definition, resolved.environment = string(proposal.Method), policy.PlacementAnywhere
		if proposal.Thing != "" {
			resolved.definition = "Campfire"
		}
		return &resolved, Verdict{}, nil
	case policy.TemperatureUnknown:
		return nil, fieldUnavailable(temperatureGap(facts.Rooms)), nil
	case policy.TemperatureNoMethod:
		return nil, BuildingReasonNoDeficit, nil
	case policy.TemperatureWait:
		return nil, BuildingTemperatureWait, nil
	case policy.TemperatureShelterNeeded:
		return nil, awaitingPlan("enclosed_sleeping_room", ""), nil
	default:
		return nil, awaitingMethod(proposal.Method), nil
	}
}

// temperatureGap names the room fact SelectTemperatureMethod found unknown.
func temperatureGap(rooms domain.Fact[policy.RoomObservation]) string {
	observed, known := rooms.Value()
	if !known {
		return "rooms"
	}
	if _, known := observed.EligibleBeds.Value(); !known {
		return "eligible_beds"
	}
	return "room_temperatures"
}

// temperatureCooling assembles the powered cooler evidence from the rooms
// reading: the Cooler planning definition (availability, draw), the colony
// power topology, the site cells the vented-wall search walks, and the
// sleepers whose comfortable ranges band each room (#1199).
func temperatureCooling(facts observation.ColonyProjection) policy.TemperatureCooling {
	cooling := policy.TemperatureCooling{CoolerAvailable: domain.Unknown[bool](), CoolerDrawW: domain.Unknown[float64](), Power: facts.PowerPlanning, Cells: facts.Cells, HeatCampfires: heatCampfires(facts), Heater: facts.Shapes.Furniture.Heater}
	if sleeping, known := facts.Facts.Sleeping.Value(); known {
		cooling.Sleepers = append(append([]policy.SleepingPerson{}, sleeping.People...), sleeping.Slaves...)
	}
	plan, planKnown := facts.LayoutPlan.Value()
	for _, shelter := range plannedInteriorRooms(facts, func(planned policy.PlannedRoom) bool { return planned.Role == policy.PlannedShelter }) {
		cooling.PlannedShelters = append(cooling.PlannedShelters, policy.ThermalShelter{Cells: rectCells(shelter.Interior), Standing: shelter.Standing, Hot: planKnown && plan.Hot})
	}
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
func temperatureOutputAllowance(ctx context.Context, journal *store.Store, goal store.WorkOwner, current domain.GenerationSnapshot, tick domain.Tick) (uint32, error) {
	methods, err := journal.LoadOwnerMethods(ctx, goal)
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
