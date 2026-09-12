package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"sort"
)

type RoutineSupplySource interface {
	ReadAllowSupplies(context.Context, *c.Identity, domain.Cell) (bridge.SupplyRead, bridge.Result, error)
}
type RoutineSupplyPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineSupplySource
}
type RoutineSupplyResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineSupplyPlanner(reviewer *RoutineReviewer, native RoutineSupplySource) (*RoutineSupplyPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineSupplyPlanner{reviewer, native}, nil
}
func (r *RoutineSupplyPlanner) Step(ctx context.Context) (RoutineSupplyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineSupplyResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}
func (r *RoutineSupplyPlanner) step(call, epoch context.Context) (RoutineSupplyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineSupplyResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineSupplyResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineSupplyResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineSupplyResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.AllowStartingSupplies {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			break
		}
	}
	if err != nil {
		return RoutineSupplyResult{}, err
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineSupplyResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineSupplyResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineSupplyResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	claims, err := p.journal.SupplyClaims(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoutineSupplyResult{}, err
	}
	started := r.reviewer.clock.Now()
	var targets []domain.SupplyAllow
	for _, cell := range review.StartingSupplies.Pending {
		read, _, err := r.native.ReadAllowSupplies(call, boundaryIdentity(state.Snapshot), cell)
		if err != nil {
			return RoutineSupplyResult{}, err
		}
		if _, err = boundaryContext(read.Context, state.Snapshot); err != nil || read.Context.GetTick() < int64(review.Tick) {
			return RoutineSupplyResult{}, ErrControl
		}
		for _, target := range read.Targets {
			if target.Supply.Cell() != cell {
				return RoutineSupplyResult{}, ErrControl
			}
			if !claims[target.Supply.Thing()] {
				targets = append(targets, target.Supply)
			}
		}
		if len(targets) >= 8 {
			break
		}
	}
	if len(targets) == 0 {
		return RoutineSupplyResult{Reason: BuildingMethodUsed}, nil
	}
	sort.Slice(targets, func(i, j int) bool { return targets[i].Thing() < targets[j].Thing() })
	if len(targets) > 8 {
		targets = targets[:8]
	}
	hash := sha256.New()
	for _, supply := range targets {
		fmt.Fprintf(hash, "%s/%s/%d/%d\n", supply.Thing(), supply.Definition(), supply.Cell().X, supply.Cell().Z)
	}
	method := domain.MethodID(fmt.Sprintf("allow-%x", hash.Sum(nil)[:16]))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-allow-%x", digest[:16]))
	var actions []domain.Action
	for i, supply := range targets {
		action, err := domain.NewSupplyAllowAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), supply)
		if err != nil {
			return RoutineSupplyResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineSupplyResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineSupplyResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineSupplyResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineSupplyResult{}, err
	}
	return RoutineSupplyResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
