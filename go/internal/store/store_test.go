package store

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"reflect"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func plan(t *testing.T, id domain.PlanID, ids ...domain.ActionID) domain.PlanSpec {
	t.Helper()
	var actions []domain.Action
	for _, id := range ids {
		b, err := domain.NewBuilding("Modded_Wall", domain.Cell{X: 3, Z: 7}, domain.East, "GraniteBlocks")
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewBuildingAction(id, b)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	p, err := domain.NewPlan(id, domain.PlanRevision(^uint64(0)), actions)
	if err != nil {
		t.Fatal(err)
	}
	return p
}
func scope() domain.GenerationSnapshot {
	return domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p", Revision: domain.PlanRevision(^uint64(0)), Direction: domain.DirectionID(^uint64(0))}
}
func open(t *testing.T, path string) *Store {
	t.Helper()
	s, err := Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = s.Close() })
	return s
}
func fixture(t *testing.T) (*Store, string) {
	t.Helper()
	path := filepath.Join(t.TempDir(), "fresh.db")
	s := open(t, path)
	if err := s.CreatePlan(context.Background(), plan(t, "p", "a", "b", "c")); err != nil {
		t.Fatal(err)
	}
	return s, path
}
func prepare(t *testing.T, s *Store, action domain.ActionID) {
	t.Helper()
	if _, err := s.Prepare(context.Background(), "p", action, scope(), 10); err != nil {
		t.Fatal(err)
	}
}

func TestReopenRetainsPlanAndAllProgress(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	prepare(t, s, "b")
	issued, err := s.Dispatch(ctx, "p", "b", scope(), 10)
	if err != nil {
		t.Fatal(err)
	}
	if issued.View().Attempt != 1 || !issued.View().Unresolved {
		t.Fatal(issued.View())
	}
	prepare(t, s, "c")
	if _, err = s.Dispatch(ctx, "p", "c", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Cancel(ctx, "p", "c"); err != nil {
		t.Fatal(err)
	}
	before, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	after, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(before, after) {
		t.Fatal("reopen altered typed plan/progress")
	}
	if after.Progress[0].View().Stage != domain.Pending || after.Progress[1].View().Stage != domain.Dispatched || after.Progress[2].View().Stage != domain.Cancelled || !after.Progress[2].View().Unresolved {
		t.Fatal(after.Progress)
	}
	if _, err = s.Dispatch(ctx, "p", "b", scope(), 11); err == nil {
		t.Fatal("restart replayed dispatch")
	}
	if _, err = s.Prepare(ctx, "p", "c", scope(), 11); err == nil {
		t.Fatal("restart revived cancellation")
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	// Windows rename proves all database handles were released.
	if err = os.Rename(path, path+".closed"); err != nil {
		t.Fatal(err)
	}
}
func TestRetryAttemptsAndReceiptsSurviveRestart(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	prepare(t, s, "a")
	p, err := s.Dispatch(ctx, "p", "a", scope(), 10)
	if err != nil {
		t.Fatal(err)
	}
	first := p.View().Attempt
	if _, err = s.RecordReceipt(ctx, "p", "a", first, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Observe(ctx, "p", domain.Observation{Action: "a", Attempt: first, Snapshot: scope(), Tick: 11, Effect: domain.EffectUnknown}, scope()); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Prepare(ctx, "p", "a", scope(), 12); err == nil {
		t.Fatal("unknown read released retry")
	}
	if _, err = s.Observe(ctx, "p", domain.Observation{Action: "a", Attempt: first, Snapshot: scope(), Tick: 12, Effect: domain.EffectAbsent}, scope()); err != nil {
		t.Fatal(err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	if _, err = s.Prepare(ctx, "p", "a", scope(), 12); err != nil {
		t.Fatal(err)
	}
	p, err = s.Dispatch(ctx, "p", "a", scope(), 12)
	if err != nil {
		t.Fatal(err)
	}
	if p.View().Attempt != first+1 {
		t.Fatal("attempt reset")
	}
	if _, err = s.RecordReceipt(ctx, "p", "a", first, domain.ReceiptAccepted); err == nil {
		t.Fatal("old receipt accepted")
	}
	if _, err = s.RecordReceipt(ctx, "p", "a", p.View().Attempt, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	p = state.Progress[0]
	if p.View().Stage != domain.AwaitingObservation || !p.View().Unresolved {
		t.Fatal("receipt completed work")
	}
	if got, known := p.View().Receipt.Value(); !known || got != domain.ReceiptAccepted {
		t.Fatal("receipt lost")
	}
}
func TestAtomicCreateAndDispatchFailureRollback(t *testing.T) {
	ctx := context.Background()
	s, _ := fixture(t)
	if err := s.CreatePlan(ctx, plan(t, "p", "new")); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	// First action insert succeeds, second conflicts: neither the plan nor first
	// action may remain after the transaction rolls back.
	if err := s.CreatePlan(ctx, plan(t, "other", "new", "a")); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err := s.LoadPlan(ctx, "other"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	if err := s.CreatePlan(ctx, plan(t, "valid", "new")); err != nil {
		t.Fatal(err)
	}
	prepare(t, s, "a")
	if _, err := s.db.ExecContext(ctx, `CREATE TRIGGER fail_dispatch BEFORE INSERT ON transitions BEGIN SELECT RAISE(ABORT,'injected disk write failure'); END`); err != nil {
		t.Fatal(err)
	}
	if got, err := s.Dispatch(ctx, "p", "a", scope(), 10); err == nil || got.View().Stage != "" {
		t.Fatal("failed persistence authorized native dispatch", err)
	}
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if state.Progress[0].View().Stage != domain.Prepared || state.Progress[0].View().Attempt != 0 {
		t.Fatal("failed transaction changed state")
	}
	if _, err = s.db.ExecContext(ctx, "DROP TRIGGER fail_dispatch"); err != nil {
		t.Fatal(err)
	}
	p, err := s.Dispatch(ctx, "p", "a", scope(), 10)
	if err != nil || p.View().Attempt != 1 {
		t.Fatal("rollback consumed attempt", err)
	}
}
func TestConcurrentDispatchExactlyOneWins(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	s, path := fixture(t)
	other := open(t, path)
	prepare(t, s, "a")
	start := make(chan struct{})
	results := make(chan error, 2)
	var wg sync.WaitGroup
	for _, db := range []*Store{s, other} {
		wg.Add(1)
		go func(db *Store) {
			defer wg.Done()
			<-start
			_, err := db.Dispatch(ctx, "p", "a", scope(), 10)
			results <- err
		}(db)
	}
	close(start)
	wg.Wait()
	close(results)
	success := 0
	for err := range results {
		if err == nil {
			success++
		}
	}
	if success != 1 {
		t.Fatalf("%d dispatches authorized", success)
	}
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if state.Progress[0].View().Attempt != 1 || !state.Progress[0].View().Unresolved {
		t.Fatal("lost update")
	}
}
func TestContendedTransactionHonorsCancellation(t *testing.T) {
	s, path := fixture(t)
	other := open(t, path)
	tx, err := s.begin(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	started := time.Now()
	_, err = other.Cancel(ctx, "p", "a")
	if err == nil {
		t.Fatal("lock did not block transaction")
	}
	if ctx.Err() == nil || time.Since(started) > time.Second {
		t.Fatal("contention ignored cancellation", err, time.Since(started))
	}
	if err = tx.Rollback(); err != nil {
		t.Fatal(err)
	}
	state, err := other.LoadPlan(context.Background(), "p")
	if err != nil {
		t.Fatal(err)
	}
	if state.Progress[0].View().Stage != domain.Pending {
		t.Fatal("cancelled transaction mutated progress")
	}
}
func TestRejectsIncompatibleAndCorruptStore(t *testing.T) {
	for _, statement := range []string{"PRAGMA user_version=999", "PRAGMA application_id=12", "PRAGMA user_version=0; PRAGMA application_id=0"} {
		t.Run(statement, func(t *testing.T) {
			s, path := fixture(t)
			if _, err := s.db.Exec(statement); err != nil {
				t.Fatal(err)
			}
			if err := s.Close(); err != nil {
				t.Fatal(err)
			}
			if reopened, err := Open(context.Background(), path); err == nil {
				reopened.Close()
				t.Fatal("incompatible database accepted")
			}
		})
	}
	for _, kind := range []string{"unknown", "illegal", "malformed", "duplicate", "unknown-field"} {
		t.Run(kind, func(t *testing.T) {
			s, _ := fixture(t)
			event := transition{Kind: "unexpected"}
			if kind == "illegal" {
				event = transition{Kind: "dispatch", Snapshot: scope(), Tick: 10}
			}
			data, err := json.Marshal(event)
			if err != nil {
				t.Fatal(err)
			}
			switch kind {
			case "malformed":
				data = []byte("{")
			case "duplicate":
				data = append([]byte(`{"Kind":"cancel",`), data[1:]...)
			case "unknown-field":
				data = append([]byte(`{"Extra":1,`), data[1:]...)
			}
			if _, err = s.db.Exec("INSERT INTO transitions(action_id,payload) VALUES(?,?)", "a", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(context.Background(), "p"); err == nil {
				t.Fatal("corrupt history accepted")
			}
		})
	}
}
