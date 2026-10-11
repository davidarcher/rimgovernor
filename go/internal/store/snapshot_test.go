package store

import (
	"context"
	"fmt"
	"path/filepath"
	"reflect"
	"testing"
	"time"
)

// A snapshot in progress holds only its own read-only connection: a journal
// transaction commits while the copy waits.
func TestSnapshotDoesNotHoldTheWritePool(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	reached, release := make(chan struct{}), make(chan struct{})
	s.snapshotReady = func() { close(reached); <-release }
	dst := filepath.Join(t.TempDir(), "copy.db")
	done := make(chan error, 1)
	go func() { done <- s.Snapshot(ctx, dst) }()
	<-reached
	wctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if _, err := s.db.ExecContext(wctx, "CREATE TABLE during_snapshot(x)"); err != nil {
		t.Fatalf("journal write blocked by a snapshot in progress: %v", err)
	}
	close(release)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

// Writers committing while snapshots run never produce a copy that splits a
// transaction: each transaction inserts one row into two tables.
func TestSnapshotIsConsistentUnderConcurrentWriter(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	if _, err := s.db.ExecContext(ctx, "CREATE TABLE pair_a(n INTEGER); CREATE TABLE pair_b(n INTEGER)"); err != nil {
		t.Fatal(err)
	}
	stop, stopped := make(chan struct{}), make(chan error, 1)
	go func() {
		for n := 0; ; n++ {
			select {
			case <-stop:
				stopped <- nil
				return
			default:
			}
			tx, err := s.db.BeginTx(ctx, nil)
			if err == nil {
				_, err = tx.ExecContext(ctx, "INSERT INTO pair_a VALUES (?)", n)
			}
			if err == nil {
				_, err = tx.ExecContext(ctx, "INSERT INTO pair_b VALUES (?)", n)
			}
			if err == nil {
				err = tx.Commit()
			} else if tx != nil {
				_ = tx.Rollback()
			}
			if err != nil {
				stopped <- err
				return
			}
		}
	}()
	dir := t.TempDir()
	for i := 0; i < 10; i++ {
		dst := filepath.Join(dir, fmt.Sprintf("copy%d.db", i))
		if err := s.Snapshot(ctx, dst); err != nil {
			t.Fatal(err)
		}
		copied := open(t, dst)
		var a, b int
		if err := copied.db.QueryRowContext(ctx, "SELECT (SELECT count(*) FROM pair_a), (SELECT count(*) FROM pair_b)").Scan(&a, &b); err != nil {
			t.Fatal(err)
		}
		if a != b {
			t.Fatalf("copy %d split a transaction: %d vs %d rows", i, a, b)
		}
	}
	close(stop)
	if err := <-stopped; err != nil {
		t.Fatalf("writer failed during snapshots: %v", err)
	}
}

// Snapshot copies an open store consistently and refuses to overwrite.
func TestSnapshotCopiesOpenStore(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	prepare(t, s, "a")
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	dst := filepath.Join(t.TempDir(), "copy.db")
	if err := s.Snapshot(ctx, dst); err != nil {
		t.Fatal(err)
	}
	if err := s.Snapshot(ctx, dst); err == nil {
		t.Fatal("a second snapshot overwrote the first")
	}
	before, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	copied := open(t, dst)
	after, err := copied.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(before, after) {
		t.Fatal("the snapshot differs from the live store")
	}
	if _, err := copied.Dispatch(ctx, "p", "a", scope(), 11); err == nil {
		t.Fatal("the copy replayed the dispatch")
	}
}
