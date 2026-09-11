package store

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LoadPlans returns one complete, deterministically ordered catalog snapshot.
// The limit is an acceptance bound, never pagination: exceeding it returns no
// plans, so callers cannot mistake a partial catalog for complete accounting.
func (s *Store) LoadPlans(ctx context.Context, limit int) ([]PlanState, error) {
	if limit < 1 || limit > 256 {
		return nil, errors.New("plan catalog limit must be 1..256")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT id FROM plans ORDER BY id LIMIT ?", limit+1)
	if err != nil {
		return nil, err
	}
	var ids []domain.PlanID
	for rows.Next() {
		var id domain.PlanID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		ids = append(ids, id)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	if len(ids) > limit {
		return nil, fmt.Errorf("plan catalog exceeds complete-accounting limit %d", limit)
	}
	states := make([]PlanState, 0, len(ids))
	for _, id := range ids {
		state, err := load(ctx, tx, id)
		if err != nil {
			return nil, err
		}
		states = append(states, state)
	}
	if err = tx.Commit(); err != nil {
		return nil, err
	}
	return states, nil
}
