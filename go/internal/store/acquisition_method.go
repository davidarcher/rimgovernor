package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func admitAcquisitionMethod(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) error {
	hasAcquisition := false
	for _, action := range plan.Actions() {
		hasAcquisition = hasAcquisition || action.Kind() == domain.AcquisitionAction
	}
	if !hasAcquisition {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != goal.Goal.Snapshot || goal.Goal.Source != domain.AutopilotGoal || len(plan.Actions()) > 8 {
		return ErrConflict
	}
	bound := false
	for _, binding := range review.Goals {
		bound = bound || (binding.Need == policy.MaintainWood || binding.Need == policy.EnsureFoodSupply) && binding.Goal == goal.Goal.ID
	}
	if !bound {
		return ErrConflict
	}
	sources := map[string]bool{}
	for _, action := range plan.Actions() {
		w, ok := action.Acquisition()
		if !ok || sources[w.Thing()] {
			return ErrConflict
		}
		sources[w.Thing()] = true
	}
	return nil
}
