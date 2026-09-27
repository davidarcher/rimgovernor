package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutinePrisonerInteractionPlanner proposes one interaction write for
// MaintainPopulation's deficit: policy.PrisonerRecruitDeficit and
// SelectPrisonerInteractionMethod read the dedicated per-cycle population
// census (RoutineFacts.Prisoners, sourced from the
// rimgovernor/observations_read_population read) since, unlike husbandry,
// no other per-cycle read already carries recruitable/interaction facts.
// Disclosed narrowing: only the Recruit interaction, and Release once the
// operator opts in with PrisonerReleaseAfterDays, are ever dispatched --
// never execution or any other player-only order; see
// policy.MaintainPopulation's doc comment for why.
type RoutinePrisonerInteractionPlanner struct {
	reviewer *RoutineReviewer
	// building shells the planned jail while a prisoner is held (#835);
	// nil for a source that cannot preview buildings.
	building *RoutineBuildingPlanner
}
type RoutinePrisonerInteractionResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutinePrisonerInteractionPlanner(reviewer *RoutineReviewer) (*RoutinePrisonerInteractionPlanner, error) {
	if reviewer == nil {
		return nil, ErrControl
	}
	r := &RoutinePrisonerInteractionPlanner{reviewer: reviewer}
	if source, ok := reviewer.native.(RoutineBuildingSource); ok {
		r.building = &RoutineBuildingPlanner{reviewer: reviewer, native: source, goal: policy.MaintainPopulation, definition: "Wall"}
	}
	return r, nil
}
func (r *RoutinePrisonerInteractionPlanner) Step(ctx context.Context) (RoutinePrisonerInteractionResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutinePrisonerInteractionPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePrisonerInteractionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePrisonerInteractionResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainPopulation {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePrisonerInteractionResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutinePrisonerInteractionResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePrisonerInteractionResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	choice := policy.SelectPrisonerInteractionMethod(read.Projection.Facts.Prisoners, read.Projection.Facts.FoodDays, r.reviewer.policy.Prisoners())
	switch choice.Reason {
	case policy.PrisonerNoDeficit:
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodUsed}, nil
	case policy.PrisonerUnknown:
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodUnknown}, nil
	}
	if result, handled, err := r.stageJail(call, epoch, state, review, goal, expected); err != nil || handled {
		return result, err
	}
	// Keyed by mode, pawn and attempt count, mirroring
	// RoutineHusbandryPlanner's method key: a fresh attempt after an
	// interrupted or failed try re-selects whichever prisoner and write is
	// currently best.
	prefix := fmt.Sprintf("%s-%s-", choice.Interaction, choice.Pawn)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	interaction, err := domain.NewPrisonerInteraction(choice.Pawn, choice.Interaction)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-prisoner-interaction-%x", digest[:16]))
	action, err := domain.NewPrisonerInteractionAction(domain.ActionID(fmt.Sprintf("%s-0", id)), interaction)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePrisonerInteractionResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	return RoutinePrisonerInteractionResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// stageJail shells the planned jail (#835) while a prisoner is held and
// no jail stands; handled is false when nothing is due, the shell was
// already tried this epoch, or native refuses a ring cell, so the
// interaction goes on.
func (r *RoutinePrisonerInteractionPlanner) stageJail(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, expected observation.Identity) (RoutinePrisonerInteractionResult, bool, error) {
	if r.building == nil {
		return RoutinePrisonerInteractionResult{}, false, nil
	}
	reading, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim](), "Wall", "Door")
	if err != nil {
		return RoutinePrisonerInteractionResult{}, false, err
	}
	facts := reading.Projection
	if prisoners, _ := facts.Facts.Prisoners.Value(); !heldPrisoner(prisoners) {
		return RoutinePrisonerInteractionResult{}, false, nil
	}
	room, owed := plannedRoomOwed(facts, policy.ModulePrison)
	if !owed {
		return RoutinePrisonerInteractionResult{}, false, nil
	}
	result, err := r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, room, plannedRoomMethod(room), "routine-planned-jail", "")
	if err != nil || result.Reason == BuildingMethodUsed || result.Reason == BuildingMethodNoSpace || result.Reason == BuildingMethodUnknown {
		return RoutinePrisonerInteractionResult{}, false, err
	}
	return RoutinePrisonerInteractionResult{Reason: result.Reason}, true, nil
}

// heldPrisoner reports a living prisoner in the census.
func heldPrisoner(prisoners []policy.PrisonerFacts) bool {
	for _, p := range prisoners {
		held, hk := p.Prisoner.Value()
		dead, dk := p.Dead.Value()
		if hk && held && dk && !dead {
			return true
		}
	}
	return false
}
