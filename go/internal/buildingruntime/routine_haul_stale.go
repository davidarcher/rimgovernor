package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// cancelStaleHaulMethods cancels every not-yet-dispatched Haul action on the
// goal's open methods whose thing is no longer a current target: ordinary
// colonists (or the player) already stored, consumed or forbade it, so the
// proposal can never complete and would otherwise hold the goal's only
// method slot forever (EvaluateHaul keeps refusing it as thing_absent). It
// A haul held as native_ineligible past the contract's deadline
// (RoutinePolicy.HaulProgress: no storage accepts the thing, nobody can
// reach it) is cancelled the same way so the goal's attempt count advances
// toward its fallbacks. It reports whether any open work remains after the
// cancellations.
func cancelStaleHaulMethods(ctx context.Context, journal *store.Store, goal store.GoalState, targets []string, now domain.Tick, contract policy.ProgressContract) (bool, error) {
	return cancelHaulMethods(ctx, journal, goal, targets, now, contract)
}

// cancelStalledHaulMethods is the stall-only half, run before the ranking
// gate: a stalled haul counts as an existing commitment, so the ranking
// never selects the goal again until the hold is settled here.
func cancelStalledHaulMethods(ctx context.Context, journal *store.Store, goal store.GoalState, now domain.Tick, contract policy.ProgressContract) error {
	_, err := cancelHaulMethods(ctx, journal, goal, nil, now, contract)
	return err
}

func cancelHaulMethods(ctx context.Context, journal *store.Store, goal store.GoalState, targets []string, now domain.Tick, contract policy.ProgressContract) (bool, error) {
	current := map[string]bool{}
	for _, id := range targets {
		current[id] = true
	}
	open := false
	for _, method := range goal.Methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return false, err
		}
		if !domain.GoalWorkOpen(plan.Progress) {
			continue
		}
		hauls := map[domain.ActionID]domain.Haul{}
		for _, action := range plan.Spec.Actions() {
			if haul, ok := action.Haul(); ok {
				hauls[action.ID()] = haul
			}
		}
		cancelled := false
		for _, progress := range plan.Progress {
			v := progress.View()
			haul, ok := hauls[v.Action]
			if !ok || v.Stage != domain.Pending && v.Stage != domain.Prepared {
				continue
			}
			if (targets == nil || current[haul.Thing()]) && !haulStalled(v, now, contract) {
				continue
			}
			if _, err = journal.Cancel(ctx, method.Plan, v.Action); err != nil {
				return false, err
			}
			cancelled = true
		}
		if cancelled {
			if plan, err = journal.LoadPlan(ctx, method.Plan); err != nil {
				return false, err
			}
		}
		open = open || domain.GoalWorkOpen(plan.Progress)
	}
	return open, nil
}

func haulStalled(v domain.ProgressView, now domain.Tick, contract policy.ProgressContract) bool {
	if contract.Deadline <= 0 {
		return false
	}
	hold, ok := v.FreshHold()
	if !ok {
		return false
	}
	for _, reason := range hold.Reasons() {
		if reason == domain.HeldNativeIneligible && contract.Expired(hold.Since, now) {
			return true
		}
	}
	return false
}

// haulAttemptCount counts every method of the goal's current epoch with the
// prefix, including retired ones: a cancelled haul retires at the next review
// and drops out of GoalState.Methods, and counting only the live bindings
// would re-derive the retired plan's ID and fail on the unique constraint.
func haulAttemptCount(ctx context.Context, journal *store.Store, goal store.GoalState, prefix string) (int, error) {
	history, err := journal.LoadGoalMethods(ctx, goal.Goal.ID, goal.Goal.Epoch)
	if err != nil {
		return 0, err
	}
	return medicalAttemptCount(history, goal.Goal.Epoch, prefix), nil
}
