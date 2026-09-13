package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"sync/atomic"
	"testing"
	"time"
)

func clockLoopFixture(t *testing.T) *ClockWorker {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	w := &ClockWorker{ctx: ctx, cancel: cancel, config: ClockWorkerConfig{PollInterval: 5 * time.Millisecond, RenewInterval: 5 * time.Millisecond, StepInterval: 5 * time.Millisecond, MaxBackoff: 80 * time.Millisecond, CallTimeout: 20 * time.Millisecond}, done: make(chan struct{}), ready: make(chan struct{}), stopGate: make(chan struct{}, 1), disable: func() error { return nil }, cleanup: func(context.Context) error { return nil }, poll: func(context.Context) (ClockPollResult, error) { return ClockPollResult{}, nil }, renew: func(context.Context) (ClockRenewResult, error) { return ClockRenewResult{}, nil }, step: func(context.Context) (ClockSchedulerResult, error) { return ClockSchedulerResult{}, nil }}
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

func (clockWorkerEventUnavailable) ReadClockEvents(context.Context, *k.EventsRequest) (*k.EventsReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unavailable")
}
func TestClockWorkerConstructorRejectsInvalidAndCancelledWithoutAttachment(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	cfg := ClockWorkerConfig{PollInterval: 10 * time.Millisecond, RenewInterval: 10 * time.Millisecond, StepInterval: 10 * time.Millisecond, MaxBackoff: time.Second, CallTimeout: 20 * time.Millisecond, PageLimit: 128}
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
	w.poll = func(context.Context) (ClockPollResult, error) {
		polls.Add(1)
		select {
		case <-allowPoll:
			return ClockPollResult{}, nil
		default:
			return ClockPollResult{}, errors.New("unavailable")
		}
	}
	w.renew = func(context.Context) (ClockRenewResult, error) { renews.Add(1); return ClockRenewResult{}, nil }
	w.step = func(ctx context.Context) (ClockSchedulerResult, error) {
		if steps.Add(1) == 1 {
			close(enteredStep)
		}
		<-ctx.Done()
		return ClockSchedulerResult{}, ctx.Err()
	}
	w.start()
	time.Sleep(35 * time.Millisecond)
	if steps.Load() != 0 || polls.Load() < 2 || renews.Load() < 2 {
		t.Fatal("barrier or independent polling failed", steps.Load(), polls.Load(), renews.Load())
	}
	close(allowPoll)
	select {
	case <-enteredStep:
	case <-time.After(time.Second):
		t.Fatal("successful poll did not unblock scheduling")
	}
	beforePoll, beforeRenew := polls.Load(), renews.Load()
	time.Sleep(30 * time.Millisecond)
	if polls.Load() <= beforePoll || renews.Load() <= beforeRenew {
		t.Fatal("slow step starved independent loops")
	}
}
func TestClockWorkerStopJoinsBeforeRetryableCleanup(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	entered, release := make(chan struct{}), make(chan struct{})
	var disabled, cleanups atomic.Int32
	w.disable = func() error { disabled.Add(1); return nil }
	w.step = func(context.Context) (ClockSchedulerResult, error) {
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
	w.step = func(context.Context) (ClockSchedulerResult, error) { steps.Add(1); return ClockSchedulerResult{}, nil }
	w.start()
	time.Sleep(120 * time.Millisecond)
	if n := steps.Load(); n < 3 || n > 7 {
		t.Fatal("unchanged decision failed bounded backoff", n)
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
