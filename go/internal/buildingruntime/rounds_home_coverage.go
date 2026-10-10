package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsHomeCoveragePlanner owns the home area: it turns the game's
// auto-expand off and edits home to the base footprint policy.PlanHomeArea
// derives, reading the reviewer's routine census.
type RoundsHomeCoveragePlanner struct {
	reviewer *Rounder
}
type RoundsHomeCoverageResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsHomeCoveragePlanner(reviewer *Rounder) (*RoundsHomeCoveragePlanner, error) {
	if reviewer == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoundsHomeCoveragePlanner: reviewer == nil || reviewer.native == nil", ErrControl)
	}
	return &RoundsHomeCoveragePlanner{reviewer}, nil
}

func (r *RoundsHomeCoveragePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsHomeCoverageResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsHomeCoverageResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsHomeCoverageResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsHomeCoverageResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainHomeCoverage)
	if err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	if !workable {
		return RoundsHomeCoverageResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsHomeCoverageResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsHomeCoverageResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsHomeCoverageResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	f := read.Projection.Facts
	planned, err := policy.PlanHomeArea(f.MapBounds, f.CurrentConstruction, claims, f.HomeCoverage, f.RangeHold)
	if err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	diff, known := planned.Value()
	if !known {
		return RoundsHomeCoverageResult{Verdict: fieldUnavailable("home_coverage")}, nil
	}
	if diff.Empty() {
		return RoundsHomeCoverageResult{Verdict: waitFor(policy.CauseMethodUsed, "home_coverage_diff")}, nil
	}
	method := diff.MethodID()
	for _, seen := range goal.Methods {
		if seen.Method == method {
			return RoundsHomeCoverageResult{Verdict: waitFor(policy.CauseMethodUsed, "home_coverage_method")}, nil
		}
	}
	id := domain.MintPlanID()
	var actions []domain.Action
	next := func() domain.ActionID { return domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))) }
	if diff.AutoOff {
		action, err := domain.NewAutoHomeAreaAction(next(), false)
		if err != nil {
			return RoundsHomeCoverageResult{}, err
		}
		actions = append(actions, action)
	}
	for _, edit := range []struct {
		op    domain.AreaOperation
		cells []domain.Cell
	}{{domain.AreaSetCells, diff.Set}, {domain.AreaClearCells, diff.Clear}} {
		if len(edit.cells) == 0 {
			continue
		}
		area, err := domain.NewArea(edit.op, "", edit.cells)
		if err != nil {
			return RoundsHomeCoverageResult{}, err
		}
		action, err := domain.NewAreaAction(next(), area)
		if err != nil {
			return RoundsHomeCoverageResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsHomeCoverageResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	return RoundsHomeCoverageResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
