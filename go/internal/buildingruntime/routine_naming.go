package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineNamingSource is the native colony census RoutineNamingPlanner reads
// to find the exact observed window/suggestions in the initial
// faction/settlement naming dialog, the same ReadColonyFacts call
// RoutineReviewer itself uses to raise the ConfirmColonyNames goal
// (observation.colony.go's own r.Facts.ColonyNaming derivation).
type RoutineNamingSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
}
type RoutineNamingPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineNamingSource
}
type RoutineNamingResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineNamingPlanner(reviewer *RoutineReviewer, native RoutineNamingSource) (*RoutineNamingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineNamingPlanner{reviewer, native}, nil
}
func (r *RoutineNamingPlanner) Step(ctx context.Context) (RoutineNamingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineNamingPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineNamingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineNamingResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineNamingResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineNamingResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.ConfirmColonyNames {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineNamingResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineNamingResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineNamingResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineNamingResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineNamingResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineNamingResult{}, ErrControl
	}
	naming := observed.Naming
	if naming == nil || naming.WindowId == nil || naming.FactionName == nil || naming.SettlementName == nil {
		return RoutineNamingResult{Reason: BuildingMethodUnknown}, nil
	}
	windowID, factionName, settlementName := naming.GetWindowId(), naming.GetFactionName(), naming.GetSettlementName()
	digestNext := sha256.Sum256([]byte(fmt.Sprintf("%d/%s/%s", windowID, factionName, settlementName)))
	method := domain.MethodID(fmt.Sprintf("naming-%x", digestNext[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineNamingResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineNamingResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-naming-%x", digest[:16]))
	value, err := domain.NewNamingConfirmation(windowID, factionName, settlementName)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	action, err := domain.NewNamingConfirmationAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineNamingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineNamingResult{}, err
	}
	if p.session.State() != state {
		return RoutineNamingResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineNamingResult{}, err
	}
	return RoutineNamingResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
