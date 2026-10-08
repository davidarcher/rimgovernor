package main

import (
	"context"
	"errors"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func newFlusher(t *testing.T, native governorStateNative, world func(context.Context) (governorWorld, bool)) *stateFlusher {
	t.Helper()
	database, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { database.Close() })
	return &stateFlusher{native: native, world: world, database: database, rebuild: &worldRebuild{database: database}}
}

// A flush rebuilds a new world from the save first, then writes every store
// blob in one batch: the save's blobs survive a flush that runs before the
// first shadow round.
func TestStateFlusherRebuildsThenPutsAllBlobsInOneBatch(t *testing.T) {
	native := &fakeGovernorState{blobs: map[string]string{"standard/a": governorStandardBlob(t, "a", "one", 3)}}
	flusher := newFlusher(t, native, func(context.Context) (governorWorld, bool) {
		return governorWorld{Colony: "one", Map: 1, Load: "l", Generation: 1}, true
	})
	if err := flusher.Flush(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(native.batches) != 1 || native.batches[0]["standard/a"] == "" {
		t.Fatalf("batches %v", native.batches)
	}
	if err := flusher.Flush(context.Background()); err != nil || len(native.batches) != 2 || native.batches[1]["standard/a"] == "" {
		t.Fatalf("second flush is not a full batch: %v %v", native.batches, err)
	}
}

func TestStateFlusherWithoutAWorldPutsNothing(t *testing.T) {
	native := &fakeGovernorState{blobs: map[string]string{"standard/a": "x"}}
	flusher := newFlusher(t, native, func(context.Context) (governorWorld, bool) { return governorWorld{}, false })
	if err := flusher.Flush(context.Background()); !errors.Is(err, errNoGovernorWorld) || len(native.batches) != 0 {
		t.Fatal("flushed without a world", err, native.batches)
	}
}

// Every Go-made save flushes first, and a failed flush refuses the save.
func TestFlushedSaveFlushesBeforeSaving(t *testing.T) {
	var order []string
	save := &flushedSave{LifecycleSave: &bridge.LifecycleSave{}, flush: func(context.Context) error {
		order = append(order, "flush")
		return nil
	}}
	// The zero LifecycleSave has no client: it refuses after the flush ran.
	if _, _, err := save.Save(context.Background(), nil); err == nil || len(order) != 1 {
		t.Fatal("save did not flush first", err, order)
	}
	boom := errors.New("flush failed")
	save.flush = func(context.Context) error { return boom }
	if _, _, err := save.Save(context.Background(), nil); !errors.Is(err, boom) {
		t.Fatal("failed flush did not refuse the save", err)
	}
}

type fakeSaveSignals struct {
	mu      sync.Mutex
	waits   []waitStep
	acks    []string
	ackErr  error
	waited  int
	ackedCh chan struct{}
}

type waitStep struct {
	token string
	err   error
}

func (f *fakeSaveSignals) WaitSaveSignal(ctx context.Context, _ time.Duration) (string, bool, error) {
	f.mu.Lock()
	if f.waited < len(f.waits) {
		step := f.waits[f.waited]
		f.waited++
		f.mu.Unlock()
		return step.token, step.token != "", step.err
	}
	f.waited++
	f.mu.Unlock()
	<-ctx.Done()
	return "", false, ctx.Err()
}

func (f *fakeSaveSignals) FlushDone(_ context.Context, token string) error {
	f.mu.Lock()
	f.acks = append(f.acks, token)
	f.mu.Unlock()
	f.ackedCh <- struct{}{}
	return f.ackErr
}

func runFlusherUntilAcks(t *testing.T, signals *fakeSaveSignals, flush func(context.Context) error, want int) {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() { defer close(done); runSaveFlusher(ctx, signals, flush, time.Millisecond) }()
	for range want {
		select {
		case <-signals.ackedCh:
		case <-time.After(30 * time.Second):
			t.Fatal("no ack")
		}
	}
	cancel()
	<-done
}

// A pre_save flushes, then acks its token; the loop re-arms after a quiet
// timeout and after a failed wait, and acks each later token the same way.
func TestSaveFlusherFlushesThenAcksAndRearms(t *testing.T) {
	signals := &fakeSaveSignals{ackedCh: make(chan struct{}, 4), waits: []waitStep{
		{token: ""}, {err: bridge.ErrDisconnected}, {token: "t1"}, {token: ""}, {token: "t2"},
	}}
	var mu sync.Mutex
	var flushes int
	runFlusherUntilAcks(t, signals, func(context.Context) error {
		mu.Lock()
		defer mu.Unlock()
		flushes++
		if len(signals.acks) != flushes-1 {
			t.Error("acked before the flush finished")
		}
		return nil
	}, 2)
	if flushes != 2 || len(signals.acks) != 2 || signals.acks[0] != "t1" || signals.acks[1] != "t2" {
		t.Fatalf("flushes %d acks %v", flushes, signals.acks)
	}
}

// A failed flush still acks, so native does not wait out its timeout.
func TestSaveFlusherAcksEvenWhenFlushFails(t *testing.T) {
	signals := &fakeSaveSignals{ackedCh: make(chan struct{}, 2), ackErr: bridge.ErrStaleSaveToken, waits: []waitStep{{token: "t1"}}}
	runFlusherUntilAcks(t, signals, func(context.Context) error { return errors.New("put failed") }, 1)
	if len(signals.acks) != 1 || signals.acks[0] != "t1" {
		t.Fatal("failed flush did not ack", signals.acks)
	}
}

// A flush that outlasts the budget is cut and still acked.
func TestSaveFlusherCutsAFlushAtTheBudget(t *testing.T) {
	signals := &fakeSaveSignals{ackedCh: make(chan struct{}, 2), waits: []waitStep{{token: "t1"}}}
	var deadline time.Time
	runFlusherUntilAcks(t, signals, func(ctx context.Context) error {
		deadline, _ = ctx.Deadline()
		return nil
	}, 1)
	if deadline.IsZero() {
		t.Fatal("flush ran without a budget")
	}
}
