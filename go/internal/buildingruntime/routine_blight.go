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
	reviewer *RoutineReviewer
	native   RoutineBlightSource
}
type RoutineBlightResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineBlightPlanner(reviewer *RoutineReviewer, native RoutineBlightSource) (*RoutineBlightPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBlightPlanner{reviewer, native}, nil
}
func (r *RoutineBlightPlanner) Step(ctx context.Context) (RoutineBlightResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineBlightResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineBlightPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBlightResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineBlightResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineBlightResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBlightResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineBlightResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.RemoveBlight {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineBlightResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineBlightResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// RemoveBlight competes for the bounded development capacity like waste
	// and the other priority>=3 autopilot goals; act only while this
	// review's arbitration selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.RemoveBlight && row.Selected
	}
	if !selected {
		return RoutineBlightResult{Reason: BuildingMethodRefused}, nil
	}
	// A plant whose designation the player cancelled (an unsuccessful cut)
	// is theirs to keep; it is not re-designated while it stands.
	claimed := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineBlightResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineBlightResult{Reason: BuildingMethodExistingWork}, nil
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
		return RoutineBlightResult{}, ErrControl
	}
	var census []policy.BlightedPlant
	byID := map[string]domain.CutPlant{}
	for _, target := range read.Targets {
		census = append(census, policy.BlightedPlant{ID: target.Plant.Plant(), Definition: target.Plant.Definition(), Cell: target.Plant.Cell(), Token: target.Token})
		byID[target.Plant.Plant()] = target.Plant
	}
	targets := policy.SelectBlightCuts(census, claimed, 8)
	if len(targets) == 0 {
		return RoutineBlightResult{Reason: BuildingMethodUsed}, nil
	}
	hash := sha256.New()
	for _, plant := range targets {
		fmt.Fprintf(hash, "%s/%s/%d/%d\n", plant.ID, plant.Definition, plant.Cell.X, plant.Cell.Z)
	}
	method := domain.MethodID(fmt.Sprintf("cut-%x", hash.Sum(nil)[:16]))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-cut-%x", digest[:16]))
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
		return RoutineBlightResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBlightResult{}, err
	}
	return RoutineBlightResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
