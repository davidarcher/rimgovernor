package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// cancelSettledRepairMethods cancels every not-yet-dispatched Repair action
// on the goal's open methods that can no longer matter: the goal's need has
// recovered (ordinary colonists mended every target, so no deficit remains
// and nothing was issued), or the action's latest hold says its structure
// is ineligible (already repaired, or gone). Left open such a method keeps
// MaintainEssentialRepairs committed in the development capacity after
// recovery, which starves every other priority>=3 goal of the slot (issue
// #61 saw the defensive layout never re-verified behind a repair of a wall
// the colonists mended themselves). It runs before the need gate, since a
// recovered goal with open work is exactly the case.
func cancelSettledRepairMethods(ctx context.Context, journal *store.Store, goal store.GoalState) error {
	recovered := goal.Goal.Need == domain.NeedRecovered
	for _, method := range goal.Methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		if !domain.GoalWorkOpen(plan.Progress) {
			continue
		}
		repairs := map[domain.ActionID]bool{}
		for _, action := range plan.Spec.Actions() {
			if _, ok := action.Repair(); ok {
				repairs[action.ID()] = true
			}
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			if !repairs[v.Action] || v.Stage != domain.Pending && v.Stage != domain.Prepared {
				continue
			}
			ineligible := false
			if hold, ok := v.FreshHold(); ok {
				for _, reason := range hold.Reasons() {
					ineligible = ineligible || reason == domain.HeldStructureIneligible
				}
			}
			if !recovered && !ineligible {
				continue
			}
			if _, err = journal.Cancel(ctx, method.Plan, v.Action); err != nil {
				return err
			}
		}
	}
	return nil
}
