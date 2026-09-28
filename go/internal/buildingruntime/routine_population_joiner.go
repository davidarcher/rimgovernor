package buildingruntime

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutinePopulationJoinerPlanner proposes one QuestAccept write for
// MaintainPopulation's joiner deficit: policy.JoinerDeficit and
// SelectJoinerMethod read the per-cycle visible quest census
// (RoutineFacts.QuestOffers, from the world-progression read the
// RoutineSource offers as RoutineQuestSource) against the bot's own
// population target (domain.PopulationTarget) and the population, sleeping
// and food facts the review already carries. Pending WandererJoins letters use the same capacity gate
// and the dialog-answer executor. Offers the colony cannot host expire.
type RoutinePopulationJoinerPlanner struct {
	reviewer *RoutineReviewer
}
type RoutinePopulationJoinerResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutinePopulationJoinerPlanner(reviewer *RoutineReviewer) (*RoutinePopulationJoinerPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutinePopulationJoinerPlanner: reviewer == nil", ErrControl)
	}
	return &RoutinePopulationJoinerPlanner{reviewer}, nil
}

func (r *RoutinePopulationJoinerPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePopulationJoinerResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePopulationJoinerResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutinePopulationJoinerResult{Reason: BuildingMethodNoReview}, nil
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
		return RoutinePopulationJoinerResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit || review.Veto(goal.Goal) != "" {
		return RoutinePopulationJoinerResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePopulationJoinerResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutinePopulationJoinerResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	facts := read.Projection.Facts
	if letter, ok := policy.SelectJoinerLetter(facts.JoinerLetters, policy.JoinerCapacity(facts.JoinerCapacity())); ok {
		return r.admitLetter(call, epoch, state, goal, letter, started)
	}
	choice := policy.SelectJoinerMethod(facts.QuestOffers, policy.JoinerCapacity(facts.JoinerCapacity()))
	switch choice.Reason {
	case policy.JoinerNoOffer, policy.JoinerNoCapacity:
		return RoutinePopulationJoinerResult{Reason: BuildingMethodUsed}, nil
	case policy.JoinerCensusUnknown:
		return RoutinePopulationJoinerResult{Reason: BuildingMethodUnknown}, nil
	}
	// Keyed by quest and attempt count, mirroring
	// RoutinePrisonerInteractionPlanner's method key: a fresh attempt after
	// an interrupted or failed try re-selects whichever offer is current.
	prefix := fmt.Sprintf("joiner-%s-", choice.Quest)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePopulationJoinerResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	accept, err := domain.NewQuestAccept(choice.Quest, "", choice.RewardChoice)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	id := domain.MintPlanID("routine-population-joiner")
	action, err := domain.NewQuestAcceptAction(domain.ActionID(fmt.Sprintf("%s-0", id)), accept)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	return RoutinePopulationJoinerResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func (r *RoutinePopulationJoinerPlanner) admitLetter(call, epoch context.Context, state ControlState, goal store.GoalState, letter policy.JoinerLetterOffer, started time.Time) (RoutinePopulationJoinerResult, error) {
	p := r.reviewer.player
	prefix := fmt.Sprintf("joiner-letter-%d-", letter.ID)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePopulationJoinerResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID("routine-joiner-letter")
	value, err := domain.NewJoinerLetterAnswer(letter.ID, letter.Label, letter.Token)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	action, err := domain.NewDialogAnswerAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: admitLetter: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	return RoutinePopulationJoinerResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
