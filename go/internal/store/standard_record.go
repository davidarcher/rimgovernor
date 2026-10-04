package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RecordStandard replaces the goal's durable record (domain.Standard.Record) at the
// goal's revision. The record rides in the goal's GovernorState blob.
func (s *Store) RecordStandard(ctx context.Context, id domain.ConcernID, revision uint64, record string) (StandardState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return StandardState{}, err
	}
	defer tx.Rollback()
	state, err := loadStandard(ctx, tx, id)
	if err != nil {
		return StandardState{}, err
	}
	if state.Revision != revision {
		return StandardState{}, ErrConflict
	}
	state, err = writeStandardRecord(ctx, tx, state, record)
	if err != nil {
		return StandardState{}, err
	}
	if err = tx.Commit(); err != nil {
		return StandardState{}, err
	}
	return state, nil
}

// CommitMethodRecord is CommitMethodReason that also replaces the
// goal's record in the same transaction, so a method and the intent it
// carries are saved together or not at all.
func (s *Store) CommitMethodRecord(ctx context.Context, id domain.ConcernID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec, record string) (StandardState, error) {
	if err := plan.Validate(); err != nil {
		return StandardState{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return StandardState{}, err
	}
	defer tx.Rollback()
	state, err := commitMethod(ctx, tx, id, revision, method, reason, plan)
	if err != nil {
		return StandardState{}, err
	}
	if state, err = writeStandardRecord(ctx, tx, state, record); err != nil {
		return StandardState{}, err
	}
	if err = tx.Commit(); err != nil {
		return StandardState{}, err
	}
	return state, nil
}

func writeStandardRecord(ctx context.Context, tx *sql.Tx, state StandardState, record string) (StandardState, error) {
	g := state.Standard
	g.Record = record
	return saveStandard(ctx, tx, state, g)
}
