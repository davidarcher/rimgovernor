package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	// BuildingSleepingUnavailable: nobody can be assigned a vacant suitable
	// bed and no bed definition is buildable now.
	BuildingSleepingUnavailable RoutineBuildingReason = "sleeping_bed_unavailable"
	// BuildingSleepingUseNeeded: every waiting colonist owns a suitable bed;
	// only observed sleep completes the goal, so nothing is dispatched.
	BuildingSleepingUseNeeded RoutineBuildingReason = "sleeping_use_needed"
)

// RoutineSleepingUpkeepPlanner answers MaintainSleeping: it transfers
// ownership of a vacant suitable bed to a colonist without one (a one-shot
// AssignBed the native side re-vets for roof, access, allowed area and the
// pawn's comfortable band), and when no bed can be assigned it stages one
// through the same building ladder the hospital walks (furnish a
// Bedroom-hosting room warm enough for the waiting colonists, else a starter
// shell first), assigning it on a later review. Neither the assignment nor
// the construction receipt recovers the goal; ReviewSleeping does, on the
// assigned pawn's observed use of that bed.
type RoutineSleepingUpkeepPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineBuildingSource
	building *RoutineBuildingPlanner
}

func NewRoutineSleepingUpkeepPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineSleepingUpkeepPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	if _, ok := native.(observation.TemperatureSource); !ok {
		return nil, ErrControl
	}
	building := &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainSleeping, definition: "Wall", shelter: true}
	return &RoutineSleepingUpkeepPlanner{reviewer: reviewer, native: native, building: building}, nil
}

func (r *RoutineSleepingUpkeepPlanner) Step(ctx context.Context) (RoutineBuildingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// sleepingRequest re-derives the sleeping targets from this step's census
// against the review's retained use history, so the choice and the build
// site share one observation.
func sleepingRequest(facts observation.ColonyProjection, review store.RoutineReview) (policy.SleepingRequest, error) {
	sleeping, err := policy.ReviewSleeping(facts.Facts.Sleeping, review.Sleeping, facts.Identity.Tick)
	if err != nil {
		return policy.SleepingRequest{}, err
	}
	definitions := make([]policy.BenchDefinition, 0, len(facts.Definitions))
	for _, d := range facts.Definitions {
		definitions = append(definitions, policy.BenchDefinition{Name: d.Name, Available: d.Available, NeedsPower: d.NeedsPower, ConstructionSkill: d.ConstructionSkill})
	}
	return policy.SleepingRequest{Targets: sleeping.Targets, Sleeping: facts.Facts.Sleeping, Rooms: facts.Rooms, Definitions: definitions}, nil
}

// selectSleeping resolves the building ladder's definition and site from
// the same census the sleeping planner chose from: only a SleepingBuild
// choice furnishes; every other outcome is reported, never built around.
func (r *RoutineBuildingPlanner) selectSleeping(facts observation.ColonyProjection, review store.RoutineReview) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	request, err := sleepingRequest(facts, review)
	if err != nil {
		return nil, "", err
	}
	choice, err := policy.SelectSleepingMethod(request)
	if err != nil {
		return nil, "", err
	}
	switch choice.Method {
	case policy.SleepingUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.SleepingNoDemand:
		return nil, BuildingSleepingUseNeeded, nil
	case policy.SleepingAssign:
		return nil, BuildingExistingFacility, nil
	case policy.SleepingUnavailable:
		return nil, BuildingSleepingUnavailable, nil
	}
	if len(choice.Cells) == 0 {
		return nil, BuildingMethodNoSpace, nil
	}
	facility, err := policy.Facility(policy.RoomRoleBedroom)
	if err != nil {
		return nil, "", err
	}
	resolved := *r
	resolved.definition = choice.Definition
	resolved.environment = policy.PlacementIndoors
	resolved.facility = &facility
	resolved.cells = choice.Cells
	resolved.sleeping = &choice
	for _, d := range facts.Definitions {
		if d.Name == resolved.definition {
			if stuff, known := d.Stuff.Value(); known {
				resolved.stuff = stuff
			}
		}
	}
	return &resolved, "", nil
}

func (r *RoutineSleepingUpkeepPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineBuildingResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineBuildingResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineBuildingResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainSleeping {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineBuildingResult{Reason: BuildingMethodNoDeficit}, nil
	}
	var observe uint32
	for _, m := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineBuildingResult{Reason: BuildingMethodExistingWork}, nil
		}
		observe = max(observe, sleepingNativeWorkTicks(plan, state.Snapshot, review.Tick))
	}
	result, err := r.decide(call, epoch, arbiter, state, review, goal)
	if err == nil && result.Reason != BuildingMethodAdmitted && result.NativeWorkTicks == 0 {
		result.NativeWorkTicks = observe
	}
	return result, err
}

// sleepingObservationBudget bounds the game time the planner asks for after
// an assignment completed so the colonist's sleep in that bed can be
// observed: one full day covers the next rest period, and the budget is not
// renewed by later observations.
const sleepingObservationBudget = 60000

// sleepingObservationSlice is one clock window inside that budget.
const sleepingObservationSlice = 600

// sleepingNativeWorkTicks is the clock window a completed assignment of this
// epoch still earns; only observed use recovers the goal, and without ticks
// nobody sleeps.
func sleepingNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) != 1 {
		return 0
	}
	p := plan.Progress[0]
	if _, ok := p.Action().BedAssign(); !ok {
		return 0
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	v := p.View()
	effect, known := v.Effect.Value()
	if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || tick < v.Tick || tick-v.Tick >= sleepingObservationBudget {
		return 0
	}
	return min(uint32(sleepingObservationSlice), uint32(sleepingObservationBudget-(tick-v.Tick)))
}

func (r *RoutineSleepingUpkeepPlanner) decide(call, epoch context.Context, arbiter *stepArbiter, state ControlState, review store.RoutineReview, goal store.GoalState) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	started := r.reviewer.clock.Now()
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineBuildingResult{}, ErrControl
	}
	reading, err := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim](), policy.SleepingBedDefinitions...)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	facts := reading.Projection
	request, err := sleepingRequest(facts, review)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	choice, err := policy.SelectSleepingMethod(request)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if clockSchedulerDebug {
		clockSchedulerLog("sleeping: choice=%+v", choice)
	}
	switch choice.Method {
	case policy.SleepingUnknown:
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	case policy.SleepingNoDemand:
		return RoutineBuildingResult{Reason: BuildingSleepingUseNeeded}, nil
	case policy.SleepingUnavailable:
		return RoutineBuildingResult{Reason: BuildingSleepingUnavailable}, nil
	case policy.SleepingBuild:
		return r.building.step(call, epoch, arbiter)
	}
	// Assign: one pawn, one bed, once per goal epoch. A method that already
	// ran this epoch (the native side refused it, or the player undid it) is
	// not retried; the next epoch reconsiders. The one exception is an
	// attempt the native side never admitted (the pawn's CAS token moves
	// whenever the colonist lies down between inspection and write): that
	// leaves no effect behind, so a bounded number of fresh attempts follow.
	method, err := r.assignMethod(call, goal, choice)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if method == "" {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	previous := domain.ClearPreviousBed()
	if choice.PreviousBed != "" {
		if previous, err = domain.KnownPreviousBed(choice.PreviousBed); err != nil {
			return RoutineBuildingResult{}, err
		}
	}
	assign, err := domain.NewBedAssign(domain.PawnID(choice.Pawn), choice.Bed, previous)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !arbiter.tryClaim(nil, "bed:"+choice.Bed) {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-sleeping-%x", digest[:16]))
	action, err := domain.NewBedAssignAction(domain.ActionID(fmt.Sprintf("%s-0", id)), assign)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineBuildingResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineBuildingResult{}, ErrControl
	}
	latest, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if latest.Revision != review.Revision || !latest.Enabled {
		return RoutineBuildingResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBuildingResult{}, err
	}
	return RoutineBuildingResult{Reason: BuildingMethodAdmitted}, nil
}

// sleepingAssignAttempts bounds the unadmitted assignment attempts one goal
// epoch may make for the same pawn and bed.
const sleepingAssignAttempts = 3

// assignMethod returns the method ID for the next assignment attempt of this
// epoch, or "" when the pair was already attempted and admitted (or the
// attempt bound is spent).
func (r *RoutineSleepingUpkeepPlanner) assignMethod(call context.Context, goal store.GoalState, choice policy.SleepingChoice) (domain.MethodID, error) {
	p := r.reviewer.player
	base := fmt.Sprintf("sleeping-assign-%s-%s", choice.Pawn, choice.Bed)
	for try := 0; try < sleepingAssignAttempts; try++ {
		method := domain.MethodID(base)
		if try > 0 {
			method = domain.MethodID(fmt.Sprintf("%s-retry%d", base, try))
		}
		existing, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method)
		if errors.Is(err, store.ErrNotFound) {
			return method, nil
		}
		if err != nil {
			return "", err
		}
		plan, err := p.journal.LoadPlan(call, existing.Plan)
		if err != nil {
			return "", err
		}
		if !sleepingAssignUnadmitted(plan.Progress) {
			return "", nil
		}
	}
	return "", nil
}

// sleepingAssignUnadmitted reports a settled plan whose every action ended
// absent: the native side refused it before admission, so nothing changed.
func sleepingAssignUnadmitted(progress []domain.Progress) bool {
	if len(progress) == 0 || domain.GoalWorkOpen(progress) {
		return false
	}
	for _, pr := range progress {
		effect, known := pr.View().Effect.Value()
		if !known || effect != domain.EffectAbsent {
			return false
		}
	}
	return true
}
