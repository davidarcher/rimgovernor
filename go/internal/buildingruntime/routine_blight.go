package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineBlightSource is the fresh blighted-plant census the planner
// proposes from: the colony read's blighted_plants rows not yet designated,
// each with the cut snapshot token the designation binds to.
type RoutineBlightSource interface {
	ReadBlightedPlants(context.Context, *c.Identity) (bridge.CutPlantRead, bridge.Result, error)
}

// RoutineBlightPlanner composes RemoveBlight's method (#245): while the
// review holds the goal in deficit and development arbitration selected it,
// one plan of up to eight CutPlant designations on the census's undesignated
// plants. The goal settles on the census emptying, never on the plan.
type RoutineBlightPlanner struct {
	reviewer *Rounder
	native   RoutineBlightSource
}
type RoutineBlightResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineBlightPlanner(reviewer *Rounder, native RoutineBlightSource) (*RoutineBlightPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineBlightPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineBlightPlanner{reviewer, native}, nil
}
func (r *RoutineBlightPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBlightResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineBlightResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineBlightResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineBlightResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineBlightResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.RemoveBlight)
	if err != nil {
		return RoutineBlightResult{}, err
	}
	if !workable {
		return RoutineBlightResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// RemoveBlight competes for the bounded development capacity like waste
	// and the other priority>=3 autopilot goals; act only while this
	// review's arbitration selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.RemoveBlight && row.Selected
	}
	if !selected {
		return RoutineBlightResult{Verdict: awaitingSlot(string(policy.RemoveBlight))}, nil
	}
	// A plant whose designation the player cancelled (an unsuccessful cut)
	// is theirs to keep; it is not re-designated while it stands.
	claimed := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineBlightResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineBlightResult{Verdict: BuildingReasonExistingWork}, nil
		}
		for _, progress := range plan.Progress {
			if cut, ok := progress.Action().CutPlant(); ok && progress.View().Stage == domain.Unsuccessful {
				claimed[cut.Plant()] = true
			}
		}
	}
	started := r.reviewer.clock.Now()
	read, _, err := r.native.ReadBlightedPlants(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoutineBlightResult{}, err
	}
	if _, err = boundary.Context(read.Context, state.Snapshot); err != nil || read.Context.GetTick() < int64(review.Tick) {
		return RoutineBlightResult{}, fmt.Errorf("%w: step: err != nil || read.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	var census []policy.BlightedPlant
	byID := map[string]domain.CutPlant{}
	for _, target := range read.Targets {
		census = append(census, policy.BlightedPlant{ID: target.Plant.Plant(), Definition: target.Plant.Definition(), Cell: target.Plant.Cell(), Eligible: true})
		byID[target.Plant.Plant()] = target.Plant
	}
	targets := policy.SelectBlightCuts(census, claimed, 8)
	if len(targets) == 0 {
		return RoutineBlightResult{Verdict: waitFor(WaitMethodUsed, "blight_cut_targets")}, nil
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
			return RoutineBlightResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineBlightResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineBlightResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineBlightResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoutineBlightResult{}, err
	}
	return RoutineBlightResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
