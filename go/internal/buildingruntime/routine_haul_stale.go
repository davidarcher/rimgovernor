package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// cancelStaleHaulMethods cancels every not-yet-dispatched Haul action on the
// goal's open methods whose thing is no longer a current target: ordinary
// colonists (or the player) already stored, consumed or forbade it, so the
// proposal would only be refused. It reports whether any open work remains
// after the cancellations.
func cancelStaleHaulMethods(ctx context.Context, journal *store.Store, goal store.GoalState, targets []string) (bool, error) {
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
		if !store.PlanOpen(plan) {
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
			if !ok || v.Stage != domain.Pending && v.Stage != domain.Prepared || current[haul.Thing()] {
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
		open = open || store.PlanOpen(plan)
	}
	return open, nil
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
