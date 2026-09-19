package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func loadDependencies(ctx context.Context, tx *sql.Tx, plan domain.PlanID) ([]domain.ActionDependency, error) {
	rows, err := tx.QueryContext(ctx, "SELECT action_id,requires_id,coupled FROM action_dependencies WHERE plan_id=? ORDER BY action_id,requires_id", plan)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var deps []domain.ActionDependency
	for rows.Next() {
		var d domain.ActionDependency
		if err = rows.Scan(&d.Action, &d.Requires, &d.Coupled); err != nil {
			return nil, err
		}
		deps = append(deps, d)
		if len(deps) > 4096 {
			return nil, ErrCapacity
		}
	}
	return deps, rows.Err()
}
