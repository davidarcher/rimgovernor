package store

import (
	"context"
	"database/sql"
	"errors"
	"testing"
)

// Cancelling the context a transaction began under must not roll it back
// under a writer that is still running: the late statement fails with its
// own cancellation, never "transaction has already been committed or rolled
// back", and the owner's Rollback still releases the transaction.
func TestBeginSurvivesCancelledContext(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	ctx, cancel := context.WithCancel(context.Background())
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	cancel()
	if _, err := tx.ExecContext(ctx, "SELECT 1"); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancelled statement: %v", err)
	}
	if _, err := tx.ExecContext(context.Background(), "SELECT 1"); errors.Is(err, sql.ErrTxDone) {
		t.Fatalf("late write hit a finished transaction: %v", err)
	}
	if err := tx.Rollback(); err != nil {
		t.Fatalf("owner rollback: %v", err)
	}
}
