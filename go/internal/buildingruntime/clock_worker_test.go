package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func clockLoopFixture(t *testing.T) *ClockWorker {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	w := &ClockWorker{ctx: ctx, cancel: cancel, config: ClockWorkerConfig{PollInterval: 5 * time.Millisecond, RenewInterval: 5 * time.Millisecond, StepInterval: 5 * time.Millisecond, MaxBackoff: 80 * time.Millisecond, PollTimeout: 20 * time.Millisecond, RenewTimeout: 20 * time.Millisecond, StepTimeout: 20 * time.Millisecond}, done: make(chan struct{}), ready: make(chan struct{}), stopGate: make(chan struct{}, 1), disable: func() error { return nil }, cleanup: func(context.Context) error { return nil }, poll: func(context.Context, time.Duration) (ClockPollResult, error) { return ClockPollResult{}, nil }, renew: func(context.Context) (ClockRenewResult, error) { return ClockRenewResult{}, nil }, step: func(context.Context, StepReason) (ClockSchedulerResult, error) { return ClockSchedulerResult{}, nil }, wake: NewWakeSignal()}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), time.Second)
		defer cancel()
		if err := w.Stop(ctx); err != nil {
			t.Error(err)
		}
	})
	return w
}

type clockWorkerEventUnavailable struct{}

func (clockWorkerEventUnavailable) ReadBundle(context.Context, *o.BundleRequest) (*o.BundleReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unavailable")
}
func TestClockWorkerConstructorRejectsInvalidAndCancelledWithoutAttachment(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	cfg := ClockWorkerConfig{PollInterval: 10 * time.Millisecond, RenewInterval: 10 * time.Millisecond, StepInterval: 10 * time.Millisecond, MaxBackoff: time.Second, PollTimeout: 20 * time.Millisecond, RenewTimeout: 20 * time.Millisecond, StepTimeout: 20 * time.Millisecond, PageLimit: 128}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := NewClockWorker(ctx, s, clockWorkerEventUnavailable{}, cfg); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	bad := cfg
	bad.RenewInterval = time.Second
	if _, err := NewClockWorker(context.Background(), s, clockWorkerEventUnavailable{}, bad); err == nil {
		t.Fatal("unsafe renewal cadence")
	}
	// The fixture lease is 1s: renew and poll must stay under lease/4, the
	// step is bounded only by the Player's 10s CallTimeout.
	for _, unsafe := range []ClockWorkerConfig{{RenewTimeout: time.Second}, {PollTimeout: time.Second}, {StepTimeout: 11 * time.Second}} {
		bad = cfg
		bad.RenewTimeout = max(bad.RenewTimeout, unsafe.RenewTimeout)
		bad.PollTimeout = max(bad.PollTimeout, unsafe.PollTimeout)
		bad.StepTimeout = max(bad.StepTimeout, unsafe.StepTimeout)
		if _, err := NewClockWorker(context.Background(), s, clockWorkerEventUnavailable{}, bad); err == nil {
			t.Fatal("unsafe loop timeout accepted", unsafe)
		}
	}
	// A long poll must leave the read a second of its timeout.
	bad = cfg
	bad.PollWait = cfg.PollTimeout
	if _, err := NewClockWorker(context.Background(), s, clockWorkerEventUnavailable{}, bad); err == nil {
		t.Fatal("poll wait consumed the whole poll timeout")
	}
	// A step budget far above lease/4 is valid.
	cfg.StepTimeout = 10 * time.Second
	w, err := NewClockWorker(context.Background(), s, clockWorkerEventUnavailable{}, cfg)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = NewClockWorker(context.Background(), s, clockWorkerEventUnavailable{}, cfg); err == nil {
		t.Fatal("duplicate attachment")
	}
	if err = w.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
}
func TestClockWorkerPollBarrierAndIndependentLoops(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	var polls, renews, steps atomic.Int32
	allowPoll := make(chan struct{})
	enteredStep := make(chan struct{})
	w.poll = func(context.Context, time.Duration) (ClockPollResult, error) {
		polls.Add(1)
		select {
		case <-allowPoll:
			return ClockPollResult{}, nil
		default:
			return ClockPollResult{}, errors.New("unavailable")
		}
	}
	w.renew = func(context.Context) (ClockRenewResult, error) { renews.Add(1); return ClockRenewResult{}, nil }
	w.step = func(ctx context.Context, _ StepReason) (ClockSchedulerResult, error) {
		if steps.Add(1) == 1 {
			close(enteredStep)
		}
		<-ctx.Done()
		return ClockSchedulerResult{}, ctx.Err()
	}
	w.start()
	// Wait on the loops' observed progress, not a wall-clock budget: under
	// CPU contention a fixed sleep saw fewer timer firings than expected.
	clockLoopWait(t, "independent polling", func() bool { return polls.Load() >= 2 && renews.Load() >= 2 })
	if steps.Load() != 0 {
		t.Fatal("barrier failed", steps.Load(), polls.Load(), renews.Load())
	}
	close(allowPoll)
	select {
	case <-enteredStep:
	case <-time.After(5 * time.Second):
		t.Fatal("successful poll did not unblock scheduling")
	}
	beforePoll, beforeRenew := polls.Load(), renews.Load()
	clockLoopWait(t, "loops independent of the slow step", func() bool { return polls.Load() > beforePoll && renews.Load() > beforeRenew })
}

// clockLoopWait polls for condition, failing after a bound that is generous
// against contention yet far below the per-test budget.
func clockLoopWait(t *testing.T, label string, condition func() bool) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for !condition() {
		if time.Now().After(deadline) {
			t.Fatal("timed out: " + label)
		}
		time.Sleep(time.Millisecond)
	}
}
func TestClockWorkerStopJoinsBeforeRetryableCleanup(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	entered, release := make(chan struct{}), make(chan struct{})
	var disabled, cleanups atomic.Int32
	w.disable = func() error { disabled.Add(1); return nil }
	w.step = func(context.Context, StepReason) (ClockSchedulerResult, error) {
		close(entered)
		<-release
		return ClockSchedulerResult{}, nil
	}
	w.cleanup = func(context.Context) error {
		if cleanups.Add(1) == 1 {
			return errors.New("pause unconfirmed")
		}
		return nil
	}
	w.start()
	<-entered
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Millisecond)
	err := w.Stop(ctx)
	cancel()
	if !errors.Is(err, context.DeadlineExceeded) || disabled.Load() == 0 || cleanups.Load() != 0 {
		t.Fatal(err, disabled.Load(), cleanups.Load())
	}
	close(release)
	if err = w.Stop(context.Background()); err == nil {
		t.Fatal("cleanup failure concealed")
	}
	if err = w.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
}
func TestClockWorkerUnchangedDecisionBacksOff(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	var steps atomic.Int32
	w.step = func(context.Context, StepReason) (ClockSchedulerResult, error) {
		steps.Add(1)
		return ClockSchedulerResult{}, nil
	}
	w.start()
	time.Sleep(120 * time.Millisecond)
	if n := steps.Load(); n < 3 || n > 7 {
		t.Fatal("unchanged decision failed bounded backoff", n)
	}
}

// A step that cleaned or reconciled an epoch is followed by the next step at
// once (issue #91): the window that stopped on its tick budget is replanned
// without a StepInterval idle. Never twice in a row, so a cleanup that keeps
// succeeding still backs off instead of spinning.
func TestClockWorkerStepsAgainAtOnceAfterSettlingAnEpoch(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	w.config.StepInterval = 40 * time.Millisecond
	w.config.MaxBackoff = 40 * time.Millisecond
	var stamps []time.Time
	var mu sync.Mutex
	w.step = func(context.Context, StepReason) (ClockSchedulerResult, error) {
		mu.Lock()
		defer mu.Unlock()
		stamps = append(stamps, time.Now())
		// Every step settles an epoch: the skip must alternate, never chain.
		return ClockSchedulerResult{Cleaned: true}, nil
	}
	w.start()
	time.Sleep(150 * time.Millisecond)
	mu.Lock()
	defer mu.Unlock()
	if len(stamps) < 4 {
		t.Fatal("too few steps", len(stamps))
	}
	if gap := stamps[1].Sub(stamps[0]); gap > 20*time.Millisecond {
		t.Fatal("cleanup step was not followed at once", gap)
	}
	if gap := stamps[2].Sub(stamps[1]); gap < 30*time.Millisecond {
		t.Fatal("two skips in a row", gap)
	}
}

func TestClockWorkerConcurrentStopCachesSuccessfulCleanup(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	entered, release := make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	w.cleanup = func(context.Context) error {
		if calls.Add(1) == 1 {
			close(entered)
		}
		<-release
		return nil
	}
	w.start()
	first, second := make(chan error, 1), make(chan error, 1)
	go func() { first <- w.Stop(context.Background()) }()
	<-entered
	go func() { second <- w.Stop(context.Background()) }()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Millisecond)
	defer cancel()
	if err := w.Stop(ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
	close(release)
	if err := <-first; err != nil {
		t.Fatal(err)
	}
	if err := <-second; err != nil {
		t.Fatal(err)
	}
	if err := w.Stop(context.Background()); err != nil || calls.Load() != 1 {
		t.Fatal(err, calls.Load())
	}
}
