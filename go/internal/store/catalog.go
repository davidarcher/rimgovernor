package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LoadPlans returns one complete, deterministically ordered active catalog snapshot.
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
	states, err := loadPlans(ctx, tx, limit)
	if err != nil {
		return nil, err
	}
	if err = tx.Commit(); err != nil {
		return nil, err
	}
	return states, nil
}

func loadPlans(ctx context.Context, tx *sql.Tx, limit int) ([]PlanState, error) {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM plans WHERE retired=0 ORDER BY id LIMIT ?", limit+1)
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
	return states, nil
}

// LatestPlanWithPrefix returns the most recently committed plan ID, retired or
// not, whose ID starts with prefix: a planner rediscovers a durable project
// (an excavation target carried in its stage plan IDs) from here after its
// goal was replaced. ErrNotFound when no such plan was ever committed.
func (s *Store) LatestPlanWithPrefix(ctx context.Context, prefix string) (domain.PlanID, error) {
	if prefix == "" {
		return "", errors.New("plan prefix required")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return "", err
	}
	defer tx.Rollback()
	var id domain.PlanID
	err = tx.QueryRowContext(ctx, "SELECT id FROM plans WHERE substr(id,1,?)=? ORDER BY rowid DESC LIMIT 1", len(prefix), prefix).Scan(&id)
	if errors.Is(err, sql.ErrNoRows) {
		return "", ErrNotFound
	}
	if err != nil {
		return "", err
	}
	return id, tx.Commit()
}

// PlanHistoryWithPrefix returns the most recently committed plans, retired or
// not, whose IDs start with prefix, newest first and at most limit of them.
// Unlike LoadPlans this is a window over history, not complete accounting: a
// shell planner reads the rings it ordered earlier in this world so it can
// recognise one of them from the walls still standing natively, whatever
// its shape. An older plan outside the window is simply not recognised.
func (s *Store) PlanHistoryWithPrefix(ctx context.Context, prefix string, limit int) ([]PlanState, error) {
	if prefix == "" {
		return nil, errors.New("plan prefix required")
	}
	if limit < 1 || limit > 256 {
		return nil, errors.New("plan history limit must be 1..256")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT id FROM plans WHERE substr(id,1,?)=? ORDER BY rowid DESC LIMIT ?", len(prefix), prefix, limit)
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
