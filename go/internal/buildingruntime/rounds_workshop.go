package buildingruntime

import (
	"context"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoundsWorkshopSource adds the fresh bench and recipe-catalog reads the
// workshop planner needs on top of the building source.
type RoundsWorkshopSource interface {
	RoundsBuildingSource
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

// workshopSelection is what the pre-observation reads settled for one step:
// the deficit resource and the bench definitions the census must describe.
type workshopProduct struct {
	resource policy.Resource
	hosts    []policy.RecipeHost
}

type workshopSelection struct {
	barrel       bool
	alternatives []workshopProduct
	resource     policy.Resource
	benches      []policy.GearBench
	hosts        []policy.RecipeHost
	candidates   []string
}

// stepWorkshops shares the facility ladder between replacement gear and resource
// targets. An admitted project or research prerequisite owns this step.
func (r *RoundsBuildingPlanner) stepWorkshops(call, epoch context.Context, arbiter *stepArbiter) (RoundsBuildingResult, error) {
	gear := *r
	gear.concern = policy.MaintainEquipment
	result, err := gear.step(call, epoch, arbiter)
	if err != nil || result.Decision.Admitted || result.NativeWorkTicks > 0 || result.Verdict == BuildingWorkshopResearch {
		return result, err
	}
	resource, err := r.step(call, epoch, arbiter)
	if err == nil && (resource.Verdict == BuildingReasonNoDeficit || resource.Verdict == BuildingReasonDisabled) {
		return result, nil
	}
	return resource, err
}

// NewRoundsWorkshopPlanner stages a production bench for a resource deficit.
// The scheduler also runs its ladder for replacement gear. For a product no
// existing bench can produce: it discovers which player-buildable
// benches host a recipe for the resource, records the research still gating
// the first of them (the review turns that into EnsureResearch's target),
// furnishes a room whose native role can host a Workshop, and when no such
// room exists stages a starter shell first, the same ladder EnsureComfort
// walks. A powered bench is staged when a generator definition is
// available; EnsureBasicPower then connects it as an unpowered consumer.
// Bills on the staged bench belong to the resource or gear planner.
func NewRoundsWorkshopPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsWorkshopPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsWorkshopPlanner: !ok", ErrControl)
	}
	if _, ok := native.(RoundsWorkshopSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsWorkshopPlanner: !ok", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainResource}, nil
}

// prepareWorkshop reads the deficit resource, the current bench census and
// the recipe catalog before the planning census is requested, so the census
// can describe exactly the candidate bench definitions. A non-empty reason
// ends the step.
func (r *RoundsBuildingPlanner) prepareWorkshop(call context.Context, state ControlState, review store.Rounds) (*workshopSelection, Verdict, error) {
	if r.concern != policy.MaintainEquipment && !r.reviewer.policy.ResourceConcernConfigured() {
		targets, err := r.reviewer.resourceTargets(call, state.Snapshot, domain.Unknown[[]policy.Amount]())
		if err != nil {
			return nil, Verdict{}, err
		}
		if len(targets) == 0 {
			return nil, BuildingReasonDisabled, nil
		}
	}
	source, ok := r.native.(RoundsWorkshopSource)
	if !ok {
		return nil, Verdict{}, fmt.Errorf("%w: prepareWorkshop: !ok", ErrControl)
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := source.ReadColonyFacts(call, identity, r.concern == policy.MaintainEquipment)
	if err != nil {
		return nil, Verdict{}, err
	}
	observed := reply.GetObserved()
	if observed == nil || bridge.ValidateColonyFacts(observed, identity) != nil {
		return nil, Verdict{}, fmt.Errorf("%w: prepareWorkshop: observed == nil || bridge.ValidateColonyFacts(observed, identity) != nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return nil, Verdict{}, fmt.Errorf("%w: prepareWorkshop: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	defs, err := gearDefinitions(call, r.native, identity)
	if err != nil {
		return nil, Verdict{}, err
	}
	finished, known := defs.Finished.Value()
	if defs.Catalog == nil || !known {
		return nil, Verdict{}, fmt.Errorf("%w: prepareWorkshop: the recipe hosts need the definition catalog and the finished research", ErrControl)
	}
	var resource policy.Resource
	var products []policy.Resource
	if r.concern == policy.MaintainEquipment {
		gear := observed.GetPlanning().GetObserved().GetGear()
		if gear == nil {
			return nil, fieldUnavailable("gear_census"), nil
		}
		things, err := frameThings(call, r.native, identity)
		if err != nil {
			return nil, Verdict{}, err
		}
		facts, known := gearObservationFacts(observed, bridge.Tables{Things: things}, defs)
		if !known {
			return nil, fieldUnavailable("gear_census"), nil
		}
		for _, pawn := range facts.Pawns {
			if candidates, known := pawn.Candidates.Value(); !known || len(candidates) > 0 {
				return nil, BuildingReasonExistingWork, nil
			}
		}
		needs, err := policy.GearReplacementNeeds(domain.Known(facts))
		if err != nil {
			return nil, Verdict{}, err
		}
		if len(needs) == 0 {
			return nil, BuildingReasonNoDeficit, nil
		}
		resource = needs[0]
		products = needs
	} else {
		stock := resourceStockFacts(observed)
		targets, err := r.reviewer.resourceTargets(call, state.Snapshot, stock)
		if err != nil {
			return nil, Verdict{}, err
		}
		var found bool
		resource, _, found, err = policy.SelectResourceTarget(targets, stock)
		if err != nil {
			return nil, Verdict{}, err
		}
		if !found {
			return nil, BuildingReasonNoDeficit, nil
		}
	}
	census, _, err := source.ReadGearBenches(call, identity)
	if resource == "Beer" {
		if observed.FermentingBarrels == nil {
			return nil, fieldUnavailable("fermenting_barrels"), nil
		}
		if observed.GetFermentingBarrels() == 0 {
			return &workshopSelection{barrel: true, resource: resource, candidates: []string{"FermentingBarrel"}}, Verdict{}, nil
		}
		items, err := r.reviewer.itemFacts(call, state.Snapshot)
		if err != nil {
			return nil, Verdict{}, err
		}
		if items.Wort == "" {
			return nil, Verdict{}, fmt.Errorf("%w: prepareWorkshop: the catalog names no wort def", ErrControl)
		}
		resource = items.Wort
	}
	if err != nil {
		return nil, Verdict{}, err
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
		hosts, err := defs.Catalog.RecipeHosts(string(product), finished)
		if err != nil {
			return nil, Verdict{}, err
		}
		request := policy.WorkshopRequest{Resource: product, Benches: domain.Known(benches), Hosts: hosts}
		snap.NoteWorkshop(call, request)
		choice, err := policy.SelectWorkshopBench(request)
		if err != nil {
			return nil, Verdict{}, err
		}
		if choice.Method == policy.WorkshopExisting {
			if err := r.recordWorkshopLadder(call, state, review, product, choice); err != nil {
				return nil, Verdict{}, err
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
	return selection, Verdict{}, nil
}

// recordWorkshopLadder persists the rung the workshop settled on so the next
// rounds can raise EnsureResearch for a research-gated bench, or
// clear that need once the bench is buildable or standing.
func (r *RoundsBuildingPlanner) recordWorkshopLadder(call context.Context, state ControlState, review store.Rounds, resource policy.Resource, choice policy.WorkshopChoice) error {
	record := store.ProductionLadderRecord{World: store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}, Tick: review.Tick, Resource: resource, Bench: choice.Definition, Recipe: choice.Recipe, Concern: r.concern}
	if choice.Method == policy.WorkshopResearch {
		record.Research = choice.Research
	}
	return r.reviewer.player.journal.SaveProductionLadder(call, record)
}

// selectWorkshop resolves the prepared candidates against the planning
// census the same way selectComfort resolves a comfort method: the chosen
// bench becomes the definition, placed indoors in a Workshop-hosting room.
func (r *RoundsBuildingPlanner) selectWorkshop(call context.Context, state ControlState, review store.Rounds, facts observation.ColonyProjection) (*RoundsBuildingPlanner, Verdict, error) {
	if r.workshop == nil {
		return nil, fieldUnavailable("workshop"), nil
	}
	if r.workshop.barrel {
		if available, known := facts.DefinitionAvailable("FermentingBarrel").Value(); !known || !available {
			return nil, BuildingWorkshopUnavailable, nil
		}
		facility, err := policy.Facility(policy.RoomRoleWorkshop)
		if err != nil {
			return nil, Verdict{}, err
		}
		resolved := *r
		resolved.definition, resolved.environment, resolved.facility = "FermentingBarrel", policy.PlacementIndoors, &facility
		return &resolved, Verdict{}, nil
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
		request := policy.WorkshopRequest{Resource: product.resource, Benches: domain.Known(r.workshop.benches), Hosts: product.hosts, Definitions: definitions, Power: generatorAvailable(available), BuilderSkill: policy.BuilderSkill(facts.WorkPawns)}
		snap.NoteWorkshop(call, request)
		candidate, err := policy.SelectWorkshopBench(request)
		if err != nil {
			return nil, Verdict{}, err
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
			return nil, Verdict{}, err
		}
	}
	switch choice.Method {
	case policy.WorkshopUnknown:
		return nil, fieldUnavailable("workshop"), nil
	case policy.WorkshopExisting:
		return nil, BuildingExistingFacility, nil
	case policy.WorkshopResearch:
		return nil, BuildingWorkshopResearch, nil
	case policy.WorkshopUnavailable:
		return nil, BuildingWorkshopUnavailable, nil
	}
	facility, err := policy.Facility(policy.RoomRoleWorkshop)
	if err != nil {
		return nil, Verdict{}, err
	}
	resolved := *r
	selectedWorkshop := *r.workshop
	selectedWorkshop.resource = resource
	resolved.workshop = &selectedWorkshop
	resolved.definition = choice.Definition
	resolved.environment = policy.PlacementIndoors
	resolved.facility = &facility
	resolved.stuff = facts.BuildStuff(resolved.definition)
	return &resolved, Verdict{}, nil
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
