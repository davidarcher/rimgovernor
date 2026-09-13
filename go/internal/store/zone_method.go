package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func admitZoneMethod(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) error {
	hasWork := false
	stockpile := false
	for _, action := range plan.Actions() {
		if zone, ok := action.ZoneCreate(); ok {
			hasWork = true
			stockpile = stockpile || zone.Kind() == domain.StockpileZone
		}
	}
	if !hasWork {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	// A stockpile zone is created once per colony in this slice, unlike the
	// bounded batches of growing-field zones EnsureFoodSupply may dispatch.
	limit := 32
	need := policy.EnsureFoodSupply
	if stockpile {
		limit = 1
		need = policy.EnsureFoodStorage
	}
	if !review.Enabled || review.Snapshot != goal.Goal.Snapshot || goal.Goal.Source != domain.AutopilotGoal || len(plan.Actions()) > limit {
		return ErrConflict
	}
	bound := false
	for _, binding := range review.Goals {
		bound = bound || binding.Need == need && binding.Goal == goal.Goal.ID
	}
	if !bound {
		return ErrConflict
	}
	cells := map[domain.Cell]bool{}
	for _, action := range plan.Actions() {
		zone, ok := action.ZoneCreate()
		if !ok || stockpile != (zone.Kind() == domain.StockpileZone) {
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
