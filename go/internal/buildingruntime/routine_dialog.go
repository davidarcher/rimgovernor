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

// RoutineDialogSource is the native colony census RoutineDialogPlanner reads
// to find the exact observed window/options of the force-pausing choice
// dialog the game opened by itself (#156), the same ReadColonyFacts call
// RoutineReviewer itself uses to raise the AnswerDialog goal
// (observation.colony.go's own r.Facts.ChoiceDialog derivation).
type RoutineDialogSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
}
type RoutineDialogPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineDialogSource
	policy   policy.DialogAnswerPolicy
}
type RoutineDialogResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// Option is the label the planner chose when it admitted a plan.
	Option string
}

func NewRoutineDialogPlanner(reviewer *RoutineReviewer, native RoutineDialogSource, answer policy.DialogAnswerPolicy) (*RoutineDialogPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineDialogPlanner{reviewer, native, answer}, nil
}
func (r *RoutineDialogPlanner) Step(ctx context.Context) (RoutineDialogResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineDialogResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineDialogPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineDialogResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineDialogResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineDialogResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineDialogResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineDialogResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.AnswerDialog {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineDialogResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineDialogResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineDialogResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineDialogResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return RoutineDialogResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineDialogResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineDialogResult{}, ErrControl
	}
	dialog := observed.Dialog
	if dialog == nil || dialog.WindowId == nil {
		return RoutineDialogResult{Reason: BuildingMethodUnknown}, nil
	}
	options := make([]policy.DialogOption, 0, len(dialog.Options))
	for _, option := range dialog.Options {
		options = append(options, policy.DialogOption{Index: option.GetIndex(), Label: option.GetLabel(), Selectable: option.GetSelectable(), Resolves: option.GetResolves()})
	}
	chosen, ok := policy.ChooseDialogOption(options, r.policy)
	if !ok {
		// Every option is disabled or a hyperlink: nothing the controller can
		// activate answers this dialog, so the hold stays with the player.
		return RoutineDialogResult{Reason: BuildingMethodExhausted}, nil
	}
	windowID := dialog.GetWindowId()
	digestNext := sha256.Sum256([]byte(fmt.Sprintf("%d/%d/%s", windowID, chosen.Index, chosen.Label)))
	method := domain.MethodID(fmt.Sprintf("dialog-%x", digestNext[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineDialogResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineDialogResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-dialog-%x", digest[:16]))
	value, err := domain.NewDialogAnswer(windowID, chosen.Index, chosen.Label)
	if err != nil {
		return RoutineDialogResult{}, err
	}
	action, err := domain.NewDialogAnswerAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineDialogResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineDialogResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDialogResult{}, err
	}
	if p.session.State() != state {
		return RoutineDialogResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineDialogResult{}, err
	}
	return RoutineDialogResult{Reason: BuildingMethodAdmitted, Plan: id, Option: chosen.Label}, nil
}
