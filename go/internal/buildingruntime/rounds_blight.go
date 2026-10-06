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

// RoundsBlightSource is the colony read the planner takes its census from:
// the blighted plants of the planning window's thing lists (#2272), the
// undesignated ones proposed for a cut.
type RoundsBlightSource interface {
	observation.ColonySource
}

// RoundsBlightPlanner composes RemoveBlight's method (#245): while the
// review holds the goal in deficit and development arbitration selected it,
// one plan of up to eight CutPlant designations on the census's undesignated
// plants. The goal settles on the census emptying, never on the plan.
type RoundsBlightPlanner struct {
	reviewer *Rounder
	native   RoundsBlightSource
}
type RoundsBlightResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsBlightPlanner(reviewer *Rounder, native RoundsBlightSource) (*RoundsBlightPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsBlightPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsBlightPlanner{reviewer, native}, nil
}
func (r *RoundsBlightPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsBlightResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBlightResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsBlightResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBlightResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsBlightResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.RemoveBlight)
	if err != nil {
		return RoundsBlightResult{}, err
	}
	if !workable {
		return RoundsBlightResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// RemoveBlight competes for the bounded development capacity like waste
	// and the other priority>=3 autopilot goals; act only while this
	// review's arbitration selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Concern == policy.RemoveBlight && row.Selected
	}
	if !selected {
		return RoundsBlightResult{Verdict: awaitingSlot(string(policy.RemoveBlight))}, nil
	}
	// A plant whose designation the player cancelled (an unsuccessful cut)
	// is theirs to keep; it is not re-designated while it stands.
	claimed := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsBlightResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsBlightResult{Verdict: BuildingReasonExistingWork}, nil
		}
		for _, progress := range plan.Progress {
			if cut, ok := progress.Action().CutPlant(); ok && progress.View().Stage == domain.Unsuccessful {
				claimed[cut.Plant()] = true
			}
		}
	}
	started := r.reviewer.clock.Now()
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoundsBlightResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoundsBlightResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsBlightResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoundsBlightResult{}, err
	}
	census, known := reading.Projection.Facts.Blight.Value()
	if !known {
		return RoundsBlightResult{Verdict: fieldUnavailable("blighted_plants")}, nil
	}
	byID := map[string]domain.CutPlant{}
	for _, plant := range census {
		cut, err := domain.NewCutPlant(plant.ID, plant.Definition, plant.Cell)
		if err != nil {
			return RoundsBlightResult{}, err
		}
		byID[plant.ID] = cut
	}
	targets := policy.SelectBlightCuts(census, claimed, 8)
	if len(targets) == 0 {
		return RoundsBlightResult{Verdict: waitFor(WaitMethodUsed, "blight_cut_targets")}, nil
	}
	hash := sha256.New()
	for _, plant := range targets {
		fmt.Fprintf(hash, "%s/%s/%d/%d\n", plant.ID, plant.Definition, plant.Cell.X, plant.Cell.Z)
	}
	method := domain.MethodID(fmt.Sprintf("cut-%x", hash.Sum(nil)[:16]))
	id := domain.MintPlanID()
	var actions []domain.Action
	for i, plant := range targets {
		action, err := domain.NewCutPlantAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), byID[plant.ID])
		if err != nil {
			return RoundsBlightResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsBlightResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsBlightResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsBlightResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsBlightResult{}, err
	}
	return RoundsBlightResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
