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

// RoutinePopulationJoinerPlanner proposes one QuestAccept write for
// MaintainPopulation's joiner deficit: policy.JoinerDeficit and
// SelectJoinerMethod read the per-cycle visible quest census
// (RoutineFacts.QuestOffers, from the world-progression read the
// RoutineSource offers as RoutineQuestSource) against the colony's declared
// population capacity (RoutineFacts.PopulationCapacity, the player's
// PopulationPolicy) and the population, sleeping and food facts the review
// already carries. Disclosed narrowing: only a ThreatReward_*_Joiner offer
// is ever accepted (policy.IsJoinerOffer); an offer the colony cannot host
// is left to expire, never rejected, and every other quest stays unanswered.
type RoutinePopulationJoinerPlanner struct {
	reviewer *RoutineReviewer
}
type RoutinePopulationJoinerResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutinePopulationJoinerPlanner(reviewer *RoutineReviewer) (*RoutinePopulationJoinerPlanner, error) {
	if reviewer == nil {
		return nil, ErrControl
	}
	return &RoutinePopulationJoinerPlanner{reviewer}, nil
}
func (r *RoutinePopulationJoinerPlanner) Step(ctx context.Context) (RoutinePopulationJoinerResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutinePopulationJoinerPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePopulationJoinerResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePopulationJoinerResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePopulationJoinerResult{}, ErrControl
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
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutinePopulationJoinerResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePopulationJoinerResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutinePopulationJoinerResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePopulationJoinerResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	// The census reading carries no journal facts; the policy is read here
	// exactly as the review read it.
	facts := read.Projection.Facts
	facts.PopulationCapacity, err = routinePopulationCapacity(call, p.journal, state.Snapshot)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
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
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePopulationJoinerResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	accept, err := domain.NewQuestAccept(choice.Quest, "", choice.RewardChoice)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-population-joiner-%x", digest[:16]))
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
		return RoutinePopulationJoinerResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	return RoutinePopulationJoinerResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
