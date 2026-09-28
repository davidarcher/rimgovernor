package store

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BatchAttempt names one action of a Prepare/Dispatch batch.
type BatchAttempt struct {
	Plan     domain.PlanID
	Action   domain.ActionID
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
}

// BatchReceipt names one receipt of a RecordReceipts batch.
type BatchReceipt struct {
	Plan    domain.PlanID
	Action  domain.ActionID
	Attempt domain.AttemptID
	Receipt domain.Receipt
}

// BatchResult is one item's outcome, in input order: the Progress its single
// call would return, or the error it would return. A failed item is left out
// of the batch without aborting the others.
type BatchResult struct {
	Progress domain.Progress
	Err      error
}

// PrepareBatch prepares every attempt in one transaction (#1040).
func (s *Store) PrepareBatch(ctx context.Context, attempts []BatchAttempt) ([]BatchResult, error) {
	return s.advanceBatch(ctx, len(attempts), func(i int) (domain.PlanID, domain.ActionID, transition) {
		a := attempts[i]
		return a.Plan, a.Action, transition{Kind: "prepare", Snapshot: a.Snapshot, Tick: a.Tick}
	})
}

// DispatchBatch authorizes every attempt in one transaction; as with
// Dispatch, only an item with a nil Err, and a nil batch error, may be sent.
func (s *Store) DispatchBatch(ctx context.Context, attempts []BatchAttempt) ([]BatchResult, error) {
	return s.advanceBatch(ctx, len(attempts), func(i int) (domain.PlanID, domain.ActionID, transition) {
		a := attempts[i]
		return a.Plan, a.Action, transition{Kind: "dispatch", Snapshot: a.Snapshot, Tick: a.Tick}
	})
}

// RecordReceipts writes every receipt in one transaction.
func (s *Store) RecordReceipts(ctx context.Context, receipts []BatchReceipt) ([]BatchResult, error) {
	return s.advanceBatch(ctx, len(receipts), func(i int) (domain.PlanID, domain.ActionID, transition) {
		r := receipts[i]
		return r.Plan, r.Action, transition{Kind: "receipt", Attempt: r.Attempt, Receipt: r.Receipt}
	})
}

// advanceBatch runs each item under its own savepoint, so a rejected item
// rolls back only its own writes. A non-nil returned error means nothing
// committed and every result is void.
func (s *Store) advanceBatch(ctx context.Context, n int, item func(int) (domain.PlanID, domain.ActionID, transition)) ([]BatchResult, error) {
	results := make([]BatchResult, n)
	if n == 0 {
		return results, nil
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	for i := range n {
		if _, err = tx.ExecContext(ctx, "SAVEPOINT batch_item"); err != nil {
			return nil, err
		}
		plan, action, event := item(i)
		next, itemErr := advanceInTransaction(ctx, tx, plan, action, event)
		if itemErr != nil {
			if _, err = tx.ExecContext(ctx, "ROLLBACK TO batch_item"); err != nil {
				return nil, fmt.Errorf("rollback batch item: %w", err)
			}
			results[i] = BatchResult{Err: itemErr}
		} else {
			results[i] = BatchResult{Progress: next}
		}
		if _, err = tx.ExecContext(ctx, "RELEASE batch_item"); err != nil {
			return nil, err
		}
	}
	if err = tx.Commit(); err != nil {
		return nil, err
	}
	return results, nil
}
