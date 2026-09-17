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
// A haul held as native_ineligible for stallTicks or longer (no storage
// accepts the thing, nobody can reach it) is cancelled the same way so the
// goal's attempt count advances toward its fallbacks. It reports whether any
// open work remains after the cancellations.
func cancelStaleHaulMethods(ctx context.Context, journal *store.Store, goal store.GoalState, targets []string, now domain.Tick, stallTicks int64) (bool, error) {
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
			if current[haul.Thing()] && !haulStalled(v, now, stallTicks) {
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

func haulStalled(v domain.ProgressView, now domain.Tick, stallTicks int64) bool {
	if stallTicks <= 0 {
		return false
	}
	hold, ok := v.FreshHold()
	if !ok {
		return false
	}
	for _, reason := range hold.Reasons() {
		if reason == domain.HeldNativeIneligible && int64(now-hold.Since) >= stallTicks {
			return true
		}
	}
	return false
}
