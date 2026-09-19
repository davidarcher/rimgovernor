package buildingruntime

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// BuildingWorkshopUnavailable: every bench that could produce the deficit
// resource needs a skilled builder or power no generator can supply; the
// deficit falls through to the resource planner's source paths.
const BuildingWorkshopUnavailable RoutineBuildingReason = "workshop_bench_unavailable"

// BuildingWorkshopResearch: the first bench that could produce the deficit
// resource waits on research; the ladder record now names the projects and
// the review raises EnsureResearch for them (issue #4 M4).
const BuildingWorkshopResearch RoutineBuildingReason = "workshop_research_needed"

// RoutineWorkshopSource adds the fresh bench and recipe-catalog reads the
// workshop planner needs on top of the building source.
type RoutineWorkshopSource interface {
	RoutineBuildingSource
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadRecipeCatalog(context.Context, *c.Identity, string) ([]policy.RecipeHost, bridge.Result, error)
}

// workshopSelection is what the pre-observation reads settled for one step:
// the deficit resource and the bench definitions the census must describe.
type workshopProduct struct {
	resource policy.Resource
	hosts    []policy.RecipeHost
}

type workshopSelection struct {
	alternatives []workshopProduct
	resource     policy.Resource
	benches      []policy.GearBench
	hosts        []policy.RecipeHost
	candidates   []string
}

// stepWorkshops shares the facility ladder between replacement gear and resource
// targets. An admitted project or research prerequisite owns this step.
func (r *RoutineBuildingPlanner) stepWorkshops(call, epoch context.Context, arbiter *stepArbiter) (RoutineBuildingResult, error) {
	gear := *r
	gear.goal = policy.MaintainEquipment
	result, err := gear.step(call, epoch, arbiter)
	if err != nil || result.Decision.Admitted || result.NativeWorkTicks > 0 || result.Reason == BuildingWorkshopResearch {
		return result, err
	}
	resource, err := r.step(call, epoch, arbiter)
	if err == nil && (resource.Reason == BuildingMethodNoDeficit || resource.Reason == BuildingMethodDisabled) {
		return result, nil
	}
	return resource, err
}

// NewRoutineWorkshopPlanner stages a production bench for a resource deficit.
// The scheduler also runs its ladder for replacement gear. For a product no
// existing bench can produce: it discovers which player-buildable
// benches host a recipe for the resource, records the research still gating
// the first of them (the review turns that into EnsureResearch's target),
// furnishes a room whose native role can host a Workshop, and when no such
// room exists stages a starter shell first, the same ladder EnsureComfort
// walks. A powered bench is staged when a generator definition is
// available; EnsureBasicPower then connects it as an unpowered consumer.
// Bills on the staged bench belong to the resource or gear planner.
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
	if r.goal != policy.MaintainEquipment && !r.reviewer.policy.ResourceGoalConfigured() {
		return nil, BuildingMethodDisabled, nil
	}
	source, ok := r.native.(RoutineWorkshopSource)
	if !ok {
		return nil, "", ErrControl
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := source.ReadColonyFacts(call, identity, r.goal == policy.MaintainEquipment, nil)
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
	var resource policy.Resource
	var products []policy.Resource
	if r.goal == policy.MaintainEquipment {
		gear := observed.GetPlanning().GetObserved().GetGear()
		if gear == nil {
			return nil, BuildingMethodUnknown, nil
		}
		facts := gearObservationFacts(gear)
		for _, pawn := range facts.Pawns {
			if candidates, known := pawn.Candidates.Value(); !known || len(candidates) > 0 {
				return nil, BuildingMethodExistingWork, nil
			}
		}
		needs := policy.GearReplacementNeeds(domain.Known(facts))
		if len(needs) == 0 {
			return nil, BuildingMethodNoDeficit, nil
		}
		resource = needs[0]
		products = needs
	} else {
		stock := resourceStockFacts(observed)
		targets, err := r.reviewer.resourceTargets(call, state.Snapshot, stock)
		if err != nil {
			return nil, "", err
		}
		var found bool
		resource, _, found, err = policy.SelectResourceTarget(targets, stock)
		if err != nil {
			return nil, "", err
		}
		if !found {
			return nil, BuildingMethodNoDeficit, nil
		}
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
	if len(products) == 0 {
		products = []policy.Resource{resource}
	}
	selection := &workshopSelection{benches: benches}
	candidates := map[string]bool{}
	for _, product := range products {
		hosts, _, err := source.ReadRecipeCatalog(call, identity, string(product))
		if err != nil {
			return nil, "", err
		}
		choice, err := policy.SelectWorkshopBench(policy.WorkshopRequest{Resource: product, Benches: domain.Known(benches), Hosts: hosts})
		if err != nil {
			return nil, "", err
		}
		if choice.Method == policy.WorkshopExisting {
			if err := r.recordWorkshopLadder(call, state, review, product, choice); err != nil {
				return nil, "", err
			}
			return nil, BuildingExistingFacility, nil
		}
		gated := policy.WorkshopResearchCandidates(product, hosts)
		if len(gated) == 0 {
			continue
		}
		if selection.resource == "" {
			selection.resource, selection.hosts = product, hosts
		} else {
			selection.alternatives = append(selection.alternatives, workshopProduct{product, hosts})
		}
		for _, name := range gated {
			candidates[name] = true
		}
	}
	if selection.resource == "" {
		return nil, BuildingWorkshopUnavailable, nil
	}
	for name := range candidates {
		selection.candidates = append(selection.candidates, name)
	}
	sort.Strings(selection.candidates)
	selection.candidates = append(selection.candidates, policy.GeneratorDefinitions...)
	return selection, "", nil
}

// recordWorkshopLadder persists the rung the workshop settled on so the next
// routine review can raise EnsureResearch for a research-gated bench, or
// clear that need once the bench is buildable or standing.
func (r *RoutineBuildingPlanner) recordWorkshopLadder(call context.Context, state ControlState, review store.RoutineReview, resource policy.Resource, choice policy.WorkshopChoice) error {
	record := store.ProductionLadderRecord{World: store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}, Tick: review.Tick, Resource: resource, Bench: choice.Definition, Recipe: choice.Recipe, Goal: r.goal}
	if choice.Method == policy.WorkshopResearch {
		record.Research = choice.Research
	}
	return r.reviewer.player.journal.SaveProductionLadder(call, record)
}

// selectWorkshop resolves the prepared candidates against the planning
// census the same way selectComfort resolves a comfort method: the chosen
// bench becomes the definition, placed indoors in a Workshop-hosting room.
func (r *RoutineBuildingPlanner) selectWorkshop(call context.Context, state ControlState, review store.RoutineReview, facts observation.ColonyProjection) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	if r.workshop == nil {
		return nil, BuildingMethodUnknown, nil
	}
	definitions := make([]policy.BenchDefinition, 0, len(facts.Definitions))
	available := map[string]domain.Fact[bool]{}
	for _, d := range facts.Definitions {
		definitions = append(definitions, policy.BenchDefinition{Name: d.Name, Available: d.Available, NeedsPower: d.NeedsPower, ConstructionSkill: d.ConstructionSkill, Research: d.Research})
		available[d.Name] = d.Available
	}
	// A viable replacement precedes an unavailable or research-gated alternative.
	// Otherwise a missing layer's armor suggestion can hide a buildable shirt.
	products := append([]workshopProduct{{r.workshop.resource, r.workshop.hosts}}, r.workshop.alternatives...)
	choice := policy.WorkshopChoice{Method: policy.WorkshopUnavailable}
	resource := r.workshop.resource
	for _, product := range products {
		candidate, err := policy.SelectWorkshopBench(policy.WorkshopRequest{Resource: product.resource, Benches: domain.Known(r.workshop.benches), Hosts: product.hosts, Definitions: definitions, Power: generatorAvailable(available), BuilderSkill: policy.BuilderSkill(facts.WorkPawns)})
		if err != nil {
			return nil, "", err
		}
		if candidate.Method == policy.WorkshopBuild || candidate.Method == policy.WorkshopExisting {
			choice, resource = candidate, product.resource
			break
		}
		if candidate.Method == policy.WorkshopResearch || choice.Method == policy.WorkshopUnavailable {
			choice, resource = candidate, product.resource
		}
	}
	if choice.Method != policy.WorkshopUnknown {
		if err := r.recordWorkshopLadder(call, state, review, resource, choice); err != nil {
			return nil, "", err
		}
	}
	switch choice.Method {
	case policy.WorkshopUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.WorkshopExisting:
		return nil, BuildingExistingFacility, nil
	case policy.WorkshopResearch:
		return nil, BuildingWorkshopResearch, nil
	case policy.WorkshopUnavailable:
		return nil, BuildingWorkshopUnavailable, nil
	}
	facility, err := policy.Facility(policy.RoomRoleWorkshop)
	if err != nil {
		return nil, "", err
	}
	resolved := *r
	selectedWorkshop := *r.workshop
	selectedWorkshop.resource = resource
	resolved.workshop = &selectedWorkshop
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

// generatorAvailable is whether any generator definition the power family
// compiles is buildable now, so a powered bench can be staged and connected;
// a generator row the census did not describe leaves it unknown.
func generatorAvailable(available map[string]domain.Fact[bool]) domain.Fact[bool] {
	result := domain.Known(false)
	for _, name := range policy.GeneratorDefinitions {
		v, known := available[name].Value()
		if !known {
			result = domain.Unknown[bool]()
		} else if v {
			return domain.Known(true)
		}
	}
	return result
}
