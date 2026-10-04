package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineHomeCoveragePlanner owns the home area (#1328): it turns the game's
// auto-expand off and edits home to the base footprint policy.PlanHomeArea
// derives, reading the reviewer's routine census.
type RoutineHomeCoveragePlanner struct {
	reviewer *Rounder
}
type RoutineHomeCoverageResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineHomeCoveragePlanner(reviewer *Rounder) (*RoutineHomeCoveragePlanner, error) {
	if reviewer == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoutineHomeCoveragePlanner: reviewer == nil || reviewer.native == nil", ErrControl)
	}
	return &RoutineHomeCoveragePlanner{reviewer}, nil
}

func (r *RoutineHomeCoveragePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineHomeCoverageResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineHomeCoverageResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineHomeCoverageResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineHomeCoverageResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainHomeCoverage)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	if !workable {
		return RoutineHomeCoverageResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineHomeCoverageResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineHomeCoverageResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineHomeCoverageResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	f := read.Projection.Facts
	planned, err := policy.PlanHomeArea(f.MapBounds, f.CurrentConstruction, claims, f.HomeCoverage)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	diff, known := planned.Value()
	if !known {
		return RoutineHomeCoverageResult{Verdict: fieldUnavailable("home_coverage")}, nil
	}
	if diff.Empty() {
		return RoutineHomeCoverageResult{Verdict: waitFor(WaitMethodUsed, "home_coverage_diff")}, nil
	}
	method := diff.MethodID()
	for _, seen := range goal.Methods {
		if seen.Method == method {
			return RoutineHomeCoverageResult{Verdict: waitFor(WaitMethodUsed, "home_coverage_method")}, nil
		}
	}
	id := domain.MintPlanID()
	var actions []domain.Action
	next := func() domain.ActionID { return domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))) }
	if diff.AutoOff {
		action, err := domain.NewAutoHomeAreaAction(next(), false)
		if err != nil {
			return RoutineHomeCoverageResult{}, err
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
			return RoutineHomeCoverageResult{}, err
		}
		action, err := domain.NewAreaAction(next(), area)
		if err != nil {
			return RoutineHomeCoverageResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineHomeCoverageResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	return RoutineHomeCoverageResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
