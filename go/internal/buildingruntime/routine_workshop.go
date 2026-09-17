package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// BuildingWorkshopUnavailable: every bench that could produce the deficit
// resource needs research, power or a skilled builder this planner does not
// stage; the deficit falls through to the resource planner's source paths.
const BuildingWorkshopUnavailable RoutineBuildingReason = "workshop_bench_unavailable"

// RoutineWorkshopSource adds the fresh bench and recipe-catalog reads the
// workshop planner needs on top of the building source.
type RoutineWorkshopSource interface {
	RoutineBuildingSource
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadRecipeCatalog(context.Context, *c.Identity, string) ([]policy.RecipeHost, bridge.Result, error)
}

// workshopSelection is what the pre-observation reads settled for one step:
// the deficit resource and the bench definitions the census must describe.
type workshopSelection struct {
	resource   policy.Resource
	benches    []policy.GearBench
	hosts      []policy.RecipeHost
	candidates []string
}

// NewRoutineWorkshopPlanner stages a production bench for a MaintainResource
// deficit no existing bench can produce: it discovers which player-buildable
// benches host a research-available recipe for the resource, furnishes a
// room whose native role can host a Workshop, and when no such room exists
// stages a starter shell first, the same ladder EnsureComfort walks. Bills on
// the staged bench belong to RoutineResourcePlanner.
func NewRoutineWorkshopPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	if _, ok := native.(observation.TemperatureSource); !ok {
		return nil, ErrControl
	}
	if _, ok := native.(RoutineWorkshopSource); !ok {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainResource, definition: "Wall", shelter: true}, nil
}

// prepareWorkshop reads the deficit resource, the current bench census and
// the recipe catalog before the planning census is requested, so the census
// can describe exactly the candidate bench definitions. A non-empty reason
// ends the step.
func (r *RoutineBuildingPlanner) prepareWorkshop(call context.Context, state ControlState, review store.RoutineReview) (*workshopSelection, RoutineBuildingReason, error) {
	if len(r.reviewer.policy.ResourceTargets) == 0 {
		return nil, BuildingMethodDisabled, nil
	}
	source, ok := r.native.(RoutineWorkshopSource)
	if !ok {
		return nil, "", ErrControl
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := source.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return nil, "", err
	}
	observed := reply.GetObserved()
	if observed == nil || bridge.ValidateColonyFacts(observed, identity) != nil {
		return nil, "", ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return nil, "", ErrControl
	}
	resource, _, ok, err := policy.SelectResourceTarget(r.reviewer.policy.ResourceTargets, resourceStockFacts(observed))
	if err != nil {
		return nil, "", err
	}
	if !ok {
		return nil, BuildingMethodNoDeficit, nil
	}
	census, _, err := source.ReadGearBenches(call, identity)
	if err != nil {
		return nil, "", err
	}
	if len(census) > 256 {
		return nil, "", ErrControl
	}
	benches := make([]policy.GearBench, 0, len(census))
	for _, row := range census {
		benches = append(benches, row.Bench)
	}
	hosts, _, err := source.ReadRecipeCatalog(call, identity, string(resource))
	if err != nil {
		return nil, "", err
	}
	selection := &workshopSelection{resource: resource, benches: benches, hosts: hosts, candidates: policy.WorkshopBenchCandidates(resource, hosts)}
	// Reuse before build: an existing bench with the recipe leaves the deficit
	// to the bill path even when the census would also allow staging one.
	choice, err := policy.SelectWorkshopBench(policy.WorkshopRequest{Resource: resource, Benches: domain.Known(benches), Hosts: hosts})
	if err != nil {
		return nil, "", err
	}
	switch choice.Method {
	case policy.WorkshopExisting:
		return nil, BuildingExistingFacility, nil
	case policy.WorkshopUnavailable:
		return nil, BuildingWorkshopUnavailable, nil
	}
	if len(selection.candidates) == 0 {
		return nil, BuildingWorkshopUnavailable, nil
	}
	return selection, "", nil
}

// selectWorkshop resolves the prepared candidates against the planning
// census the same way selectComfort resolves a comfort method: the chosen
// bench becomes the definition, placed indoors in a Workshop-hosting room.
func (r *RoutineBuildingPlanner) selectWorkshop(facts observation.ColonyProjection) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	if r.workshop == nil {
		return nil, BuildingMethodUnknown, nil
	}
	definitions := make([]policy.BenchDefinition, 0, len(facts.Definitions))
	for _, d := range facts.Definitions {
		definitions = append(definitions, policy.BenchDefinition{Name: d.Name, Available: d.Available, NeedsPower: d.NeedsPower, ConstructionSkill: d.ConstructionSkill})
	}
	choice, err := policy.SelectWorkshopBench(policy.WorkshopRequest{Resource: r.workshop.resource, Benches: domain.Known(r.workshop.benches), Hosts: r.workshop.hosts, Definitions: definitions})
	if err != nil {
		return nil, "", err
	}
	switch choice.Method {
	case policy.WorkshopUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.WorkshopExisting:
		return nil, BuildingExistingFacility, nil
	case policy.WorkshopUnavailable:
		return nil, BuildingWorkshopUnavailable, nil
	}
	facility, err := policy.Facility(policy.RoomRoleWorkshop)
	if err != nil {
		return nil, "", err
	}
	resolved := *r
	resolved.definition = choice.Definition
	resolved.environment = policy.PlacementIndoors
	resolved.facility = &facility
	for _, d := range facts.Definitions {
		if d.Name == resolved.definition {
			if stuff, known := d.Stuff.Value(); known {
				resolved.stuff = stuff
			}
		}
	}
	return &resolved, "", nil
}
