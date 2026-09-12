package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func admitZoneMethod(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) error {
	hasWork := false
	for _, action := range plan.Actions() {
		hasWork = hasWork || action.Kind() == domain.ZoneCreateAction
	}
	if !hasWork {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != goal.Goal.Snapshot || goal.Goal.Source != domain.AutopilotGoal || len(plan.Actions()) > 32 {
		return ErrConflict
	}
	bound := false
	for _, binding := range review.Goals {
		bound = bound || binding.Need == policy.EnsureFoodSupply && binding.Goal == goal.Goal.ID
	}
	if !bound {
		return ErrConflict
	}
	cells := map[domain.Cell]bool{}
	for _, action := range plan.Actions() {
		zone, ok := action.ZoneCreate()
		if !ok {
			return ErrConflict
		}
		for _, cell := range zone.Cells() {
			if cells[cell] {
				return ErrConflict
			}
			cells[cell] = true
		}
	}
	return nil
}
