package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func cancelSettledRepairMethods(ctx context.Context, journal *store.Store, goal store.StandardState) error {
	if goal.Standard.Finding != domain.FindingMet {
		return nil
	}
	for _, method := range goal.Methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		if !store.PlanOpen(plan) {
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
			if _, err = journal.Cancel(ctx, method.Plan, v.Action); err != nil {
				return err
			}
		}
	}
	return nil
}
