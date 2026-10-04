package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Retirement retains identities, methods and progress. Only invalidated
// autopilot goals without observation or cleanup obligations leave capacity.
func retireRoundsStandards(ctx context.Context, tx *sql.Tx, retained map[domain.ConcernID]bool) error {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM standards WHERE retired=0 ORDER BY id LIMIT 257")
	if err != nil {
		return err
	}
	var ids []domain.ConcernID
	for rows.Next() {
		var id domain.ConcernID
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
	if len(ids) > maxActiveStandards {
		return ErrCapacity
	}
	for _, id := range ids {
		if retained[id] {
			continue
		}
		g, err := loadStandard(ctx, tx, id)
		if err != nil {
			return err
		}
		if g.Standard.Status != domain.StandardVoided {
			continue
		}
		open, err := standardOpenWork(ctx, tx, g)
		if err != nil {
			return err
		}
		if open {
			continue
		}
		if _, err = tx.ExecContext(ctx, "UPDATE standards SET retired=1 WHERE id=?", id); err != nil {
			return err
		}
	}
	return nil
}
