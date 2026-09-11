package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Retirement retains identities, methods and progress. Only invalidated
// autopilot goals without observation or cleanup obligations leave capacity.
func retireRoutineGoals(ctx context.Context, tx *sql.Tx, retained map[domain.GoalID]bool) error {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM goals WHERE retired=0 ORDER BY id LIMIT 257")
	if err != nil {
		return err
	}
	var ids []domain.GoalID
	for rows.Next() {
		var id domain.GoalID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return err
		}
		ids = append(ids, id)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return err
	}
	if len(ids) > 256 {
		return ErrCapacity
	}
	for _, id := range ids {
		if retained[id] {
			continue
		}
		g, err := loadGoal(ctx, tx, id)
		if err != nil {
			return err
		}
		if g.Goal.Source != domain.AutopilotGoal || g.Goal.Status != domain.GoalInvalidated {
			continue
		}
		open, err := goalOpenWork(ctx, tx, g)
		if err != nil {
			return err
		}
		if open {
			continue
		}
		if _, err = tx.ExecContext(ctx, "UPDATE goals SET retired=1 WHERE id=?", id); err != nil {
			return err
		}
	}
	return nil
}
