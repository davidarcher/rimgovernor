package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// cancelStaleHaulMethods cancels every not-yet-dispatched Haul action on the
// goal's open methods whose thing is no longer a current target: ordinary
// colonists (or the player) already stored, consumed or forbade it, so the
// proposal can never complete and would otherwise hold the goal's only
// method slot forever (EvaluateHaul keeps refusing it as thing_absent). It
// reports whether any open work remains after the cancellations.
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
			if !ok || current[haul.Thing()] || v.Stage != domain.Pending && v.Stage != domain.Prepared {
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
