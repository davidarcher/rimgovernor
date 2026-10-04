package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RecordGoal replaces the goal's durable record (domain.Goal.Record) at the
// goal's revision. The record rides in the goal's GovernorState blob.
func (s *Store) RecordGoal(ctx context.Context, id domain.ConcernID, revision uint64, record string) (GoalState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := loadGoal(ctx, tx, id)
	if err != nil {
		return GoalState{}, err
	}
	if state.Revision != revision {
		return GoalState{}, ErrConflict
	}
	state, err = writeGoalRecord(ctx, tx, state, record)
	if err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return state, nil
}

// CommitGoalMethodRecord is CommitGoalMethodReason that also replaces the
// goal's record in the same transaction, so a method and the intent it
// carries are saved together or not at all.
func (s *Store) CommitGoalMethodRecord(ctx context.Context, id domain.ConcernID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec, record string) (GoalState, error) {
	if err := plan.Validate(); err != nil {
		return GoalState{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := commitGoalMethod(ctx, tx, id, revision, method, reason, plan)
	if err != nil {
		return GoalState{}, err
	}
	if state, err = writeGoalRecord(ctx, tx, state, record); err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return state, nil
}

func writeGoalRecord(ctx context.Context, tx *sql.Tx, state GoalState, record string) (GoalState, error) {
	g := state.Goal
	g.Record = record
	return saveGoal(ctx, tx, state, g)
}
