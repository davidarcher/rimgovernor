package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strings"

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

// SeedStandard inserts a fresh goal row for fixtures; the rounds mints
// every production goal (createStandard).
func (s *Store) SeedStandard(ctx context.Context, g domain.Standard) error {
	if g.Status != domain.StandardOpen || g.Episode != 0 || g.Finding != domain.FindingUnclear || g.RecoveryObserved {
		return errors.New("new goal must start without completion evidence")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if err = createStandard(ctx, tx, g); err != nil {
		return err
	}
	if err = tx.Commit(); err != nil {
		return err
	}
	s.notifyGoalsWritten()
	return nil
}

// SeedPlanMethod records method on a committed plan without a goal_methods
// row, for fixtures seeding the history PlanHistoryWithMethods reads.
func (s *Store) SeedPlanMethod(ctx context.Context, plan domain.PlanID, method domain.MethodID) error {
	result, err := s.db.ExecContext(ctx, "UPDATE plans SET method_id=? WHERE id=?", method, plan)
	if err != nil {
		return err
	}
	if n, err := result.RowsAffected(); err != nil || n != 1 {
		return errors.Join(ErrNotFound, err)
	}
	return nil
}

// PlanHistoryWithMethods returns the most recently committed plans, retired
// or not, admitted under a method id matching one of the GLOB patterns
// (#987: the stored method, never the minted plan id), newest first and at
// most limit of them.
// Unlike LoadPlans this is a window over history, not complete accounting: a
// shell planner reads the rings it ordered earlier in this world so it can
// recognise one of them from the walls still standing natively, whatever
// its shape. An older plan outside the window is simply not recognised.
func (s *Store) PlanHistoryWithMethods(ctx context.Context, limit int, patterns ...string) ([]PlanState, error) {
	if len(patterns) == 0 {
		return nil, errors.New("method pattern required")
	}
	clauses, args := make([]string, len(patterns)), make([]any, 0, len(patterns)+1)
	for i, p := range patterns {
		if p == "" {
			return nil, errors.New("method pattern required")
		}
		clauses[i], args = "method_id GLOB ?", append(args, p)
	}
	if limit < 1 || limit > 256 {
		return nil, errors.New("plan history limit must be 1..256")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT id FROM plans WHERE "+strings.Join(clauses, " OR ")+" ORDER BY rowid DESC LIMIT ?", append(args, limit)...)
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
