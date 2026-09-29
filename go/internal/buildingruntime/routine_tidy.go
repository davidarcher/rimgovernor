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

// RoutineTidyPlanner executes the TidyLayout review's furniture proposal
// (#611, #809) one room at a time. A refused or retired method journals the
// tidy abandoned so the item is never proposed again.
type RoutineTidyPlanner struct {
	reviewer *RoutineReviewer
}
type RoutineTidyResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineTidyPlanner(reviewer *RoutineReviewer) (*RoutineTidyPlanner, error) {
	if reviewer == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoutineTidyPlanner: reviewer == nil || reviewer.native == nil", ErrControl)
	}
	return &RoutineTidyPlanner{reviewer}, nil
}

func tidyMethodID(item, phase string) domain.MethodID {
	sum := sha256.Sum256([]byte(item))
	return domain.MethodID(fmt.Sprintf("tidy-%s-%x", phase, sum[:12]))
}

func (r *RoutineTidyPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineTidyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineTidyResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineTidyResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot || review.Layout == nil {
		return RoutineTidyResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.TidyLayout {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || review.Veto(goal.Goal) != "" {
		return RoutineTidyResult{Reason: BuildingMethodNoDeficit}, nil
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineTidyResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	tidies, err := p.journal.LayoutTidies(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	for _, t := range tidies {
		if t.Status == store.LayoutTidyMoving && t.Kind == policy.TidyFurniture {
			return r.finishFurniture(call, state, expected.Tick, tidies, t.PlanID)
		}
	}
	if goal.Goal.Need != domain.NeedDeficit {
		return RoutineTidyResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineTidyResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineTidyResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	proposal := review.Layout.Proposal
	if proposal == nil {
		return RoutineTidyResult{Reason: BuildingMethodUnknown}, nil
	}
	for _, t := range tidies {
		if t.Item == proposal.Item.ID {
			return RoutineTidyResult{Reason: BuildingMethodUsed}, nil
		}
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if proposal.Item.Kind != policy.TidyFurniture {
		return RoutineTidyResult{Reason: BuildingMethodUnknown}, nil
	}
	return r.move(call, epoch, state, goal, read, *proposal)
}

// commit journals one goal method after the freshness checks every planner
// makes between its native reads and its write.
func (r *RoutineTidyPlanner) commit(call, epoch context.Context, state ControlState, goal store.GoalState, started observation.RoutineReading, method domain.MethodID, plan domain.PlanSpec) error {
	p := r.reviewer.player
	if err := p.current(call, epoch); err != nil {
		return err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(started.StartedAt) || now.Sub(started.StartedAt) > r.reviewer.maxAge {
		return fmt.Errorf("%w: commit: p.session.State() != state || now.Before(started.StartedAt) || now.Sub(started.StartedAt) > r.reviewer.maxAge", ErrControl)
	}
	_, err := p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan)
	return err
}
