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

// RoundsTidyPlanner executes the TidyLayout review's furniture proposal
// (#611, #809) one room at a time. A refused or retired method journals the
// tidy abandoned so the item is never proposed again.
type RoundsTidyPlanner struct {
	reviewer *Rounder
}
type RoundsTidyResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsTidyPlanner(reviewer *Rounder) (*RoundsTidyPlanner, error) {
	if reviewer == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoundsTidyPlanner: reviewer == nil || reviewer.native == nil", ErrControl)
	}
	return &RoundsTidyPlanner{reviewer}, nil
}

func tidyMethodID(item, phase string) domain.MethodID {
	sum := sha256.Sum256([]byte(item))
	return domain.MethodID(fmt.Sprintf("tidy-%s-%x", phase, sum[:12]))
}

func (r *RoundsTidyPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsTidyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsTidyResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsTidyResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsTidyResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot || review.Layout == nil {
		return RoundsTidyResult{Verdict: BuildingReasonNoReview}, nil
	}
	var goal store.StandardState
	found := false
	for _, binding := range review.Standards {
		if binding.Concern == policy.TidyLayout {
			goal, err = p.journal.LoadStandard(call, binding.Standard)
			found = true
			break
		}
	}
	if err != nil {
		return RoundsTidyResult{}, err
	}
	if !found || goal.Standard.Status != domain.StandardOpen || review.Veto(goal.Standard) != "" {
		return RoundsTidyResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsTidyResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsTidyResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	tidies, err := p.journal.LayoutTidies(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsTidyResult{}, err
	}
	for _, t := range tidies {
		if t.Status == store.LayoutTidyMoving && t.Kind == policy.TidyFurniture {
			return r.finishFurniture(call, state, expected.Tick, tidies, t.PlanID)
		}
	}
	if goal.Standard.Finding != domain.FindingUnmet {
		return RoundsTidyResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsTidyResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsTidyResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	proposal := review.Layout.Proposal
	if proposal == nil {
		if !review.Layout.Known {
			return RoundsTidyResult{Verdict: fieldUnavailable("build_tier")}, nil
		}
		// A known review with no proposal and the need still active is a
		// re-site in flight (policy.PlanTidyLayout).
		return RoundsTidyResult{Verdict: BuildingReasonExistingWork}, nil
	}
	for _, t := range tidies {
		if t.Item == proposal.Item.ID {
			return RoundsTidyResult{Verdict: waitFor(WaitMethodUsed, "tidy_item")}, nil
		}
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsTidyResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoundsTidyResult{}, err
	}
	if proposal.Item.Kind != policy.TidyFurniture {
		return RoundsTidyResult{}, fmt.Errorf("unsupported tidy item kind %q", proposal.Item.Kind)
	}
	return r.move(call, epoch, state, goal, read, *proposal)
}

// commit journals one method after the freshness checks every planner
// makes between its native reads and its write.
func (r *RoundsTidyPlanner) commit(call, epoch context.Context, state ControlState, goal store.StandardState, started observation.RoundsReading, method domain.MethodID, plan domain.PlanSpec) error {
	p := r.reviewer.player
	if err := p.current(call, epoch); err != nil {
		return err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(started.StartedAt) || now.Sub(started.StartedAt) > r.reviewer.maxAge {
		return fmt.Errorf("%w: commit: p.session.State() != state || now.Before(started.StartedAt) || now.Sub(started.StartedAt) > r.reviewer.maxAge", ErrControl)
	}
	_, err := p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan)
	return err
}
