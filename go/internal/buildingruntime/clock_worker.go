package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"sync"
	"time"
)

// ClockWorker owns only its three loops. Session retains native capabilities,
// the profile lock and journal until Stop has joined and cleanup succeeds.
type ClockWorker struct {
	ctx        context.Context
	cancel     context.CancelFunc
	config     ClockWorkerConfig
	done       chan struct{}
	ready      chan struct{}
	stopGate   chan struct{}
	stopped    bool
	disable    func() error
	cleanup    func(context.Context) error
	poll       func(context.Context) (ClockPollResult, error)
	renew      func(context.Context) (ClockRenewResult, error)
	step       func(context.Context) (ClockSchedulerResult, error)
	stopParent func() bool
}

func NewClockWorker(ctx context.Context, scheduler *ClockScheduler, nativeEvents ClockEventNative, config ClockWorkerConfig) (*ClockWorker, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if scheduler == nil || nativeEvents == nil {
		return nil, ErrControl
	}
	// The authority Mode has no time-based expiry, so the worker's cadence is
	// bounded only by the clock's own requested lease duration.
	lease := time.Duration(scheduler.config.Start.LeaseMS) * time.Millisecond
	if config.PollInterval <= 0 || config.RenewInterval <= 0 || config.StepInterval <= 0 || config.CallTimeout <= 0 || config.PollInterval > lease/4 || config.RenewInterval > lease/4 || config.CallTimeout > lease/4 || config.CallTimeout > scheduler.player.config.CallTimeout || config.MaxBackoff < config.StepInterval || config.MaxBackoff > time.Minute || config.PageLimit < 1 || config.PageLimit > 128 {
		return nil, ErrControl
	}
	lifetime, cancel := context.WithCancel(ctx)
	w := &ClockWorker{ctx: lifetime, cancel: cancel, config: config, done: make(chan struct{}), ready: make(chan struct{}), stopGate: make(chan struct{}, 1), disable: scheduler.session.disableClockWorker, cleanup: scheduler.session.CleanupClock, step: scheduler.Step, renew: scheduler.RenewEpoch}
	w.poll = func(ctx context.Context) (ClockPollResult, error) {
		return scheduler.PollEvents(ctx, nativeEvents, config.PageLimit)
	}
	w.stopParent = context.AfterFunc(scheduler.player.lifetime, cancel)
	if err := errors.Join(ctx.Err(), scheduler.player.lifetime.Err()); err != nil {
		cancel()
		w.stopParent()
		close(w.done)
		return nil, err
	}
	if err := scheduler.session.attachClockWorker(w); err != nil {
		cancel()
		w.stopParent()
		close(w.done)
		return nil, err
	}
	w.start()
	return w, nil
}

func (w *ClockWorker) start() {
	var joined sync.WaitGroup
	joined.Add(4)
	// Join cancellation invalidation too, so it cannot touch Session after Stop.
	go func() { defer joined.Done(); <-w.ctx.Done(); _ = w.disable() }()
	go func() { defer joined.Done(); w.pollLoop() }()
	go func() { defer joined.Done(); w.renewLoop() }()
	go func() { defer joined.Done(); w.stepLoop() }()
	go func() { joined.Wait(); close(w.done) }()
}

// Stop can be retried after a deadline or cleanup failure. It does not take the
// Player gate or close Player, and never releases a resource still used by a loop.
func (w *ClockWorker) Stop(ctx context.Context) error {
	w.cancel()
	select {
	case w.stopGate <- struct{}{}:
	case <-ctx.Done():
		return ctx.Err()
	}
	defer func() { <-w.stopGate }()
	if w.stopped {
		return nil
	}
	disabled := w.disable()
	select {
	case <-w.done:
	case <-ctx.Done():
		return errors.Join(disabled, ctx.Err())
	}
	if w.stopParent != nil {
		w.stopParent()
	}
	err := errors.Join(disabled, w.cleanup(ctx))
	if err == nil {
		w.stopped = true
	}
	return err
}

func (w *ClockWorker) wait(delay time.Duration) bool {
	timer := time.NewTimer(delay)
	defer timer.Stop()
	select {
	case <-w.ctx.Done():
		return false
	case <-timer.C:
		return w.ctx.Err() == nil
	}
}
func (w *ClockWorker) pollLoop() {
	ready := false
	for w.ctx.Err() == nil {
		call, cancel := context.WithTimeout(w.ctx, w.config.CallTimeout)
		_, err := w.poll(call)
		cancel()
		if err == nil && !ready && w.ctx.Err() == nil {
			close(w.ready)
			ready = true
		}
		if !w.wait(w.config.PollInterval) {
			return
		}
	}
}
func (w *ClockWorker) renewLoop() {
	for w.wait(w.config.RenewInterval) {
		call, cancel := context.WithTimeout(w.ctx, w.config.CallTimeout)
		_, _ = w.renew(call)
		cancel()
	}
}

type clockStepKey struct {
	request, phase, reasons, failure     string
	failed, running, reconciled, cleaned bool
}

func clockWorkerKey(result ClockSchedulerResult, err error) clockStepKey {
	key := clockStepKey{failed: err != nil, running: result.Running, reconciled: result.Reconciled, cleaned: result.Cleaned}
	if err != nil {
		// A different failure is a state change worth one more log line:
		// otherwise a planner error that follows the routine startup
		// authority refusal is never surfaced at all.
		key.failure = err.Error()
	}
	if result.Attempt != nil {
		key.request = result.Attempt.Intent.RequestID
		key.phase = string(result.Attempt.Phase)
	}
	reasons := make([]string, len(result.Decision.Refused))
	for i, r := range result.Decision.Refused {
		reasons[i] = string(r)
	}
	key.reasons = strings.Join(reasons, "\x00")
	return key
}
func (w *ClockWorker) stepLoop() {
	select {
	case <-w.ctx.Done():
		return
	case <-w.ready:
	}
	delay := w.config.StepInterval
	var previous clockStepKey
	havePrevious := false
	for w.ctx.Err() == nil {
		call, cancel := context.WithTimeout(w.ctx, w.config.CallTimeout)
		result, err := w.step(call)
		cancel()
		key := clockWorkerKey(result, err)
		clockSchedulerLog("step done: err=%v", err)
		// Unconditionally surface which planner failed and why -- stepPlanners
		// wraps each planner's error with its own name (clock_scheduler.go), so
		// this is diagnosable without RIMGOVERNOR_CLOCK_DEBUG=1. Gated on state
		// change (like the backoff decision below) so a sustained failure logs
		// once, not every StepInterval. See issue #45.
		if err != nil && (!havePrevious || key != previous) {
			fmt.Fprintf(os.Stderr, "[clock-worker] step failed: %v\n", err)
		}
		if havePrevious && key == previous {
			delay = min(w.config.MaxBackoff, delay*2)
		} else {
			delay = w.config.StepInterval
		}
		previous, havePrevious = key, true
		if !w.wait(delay) {
			return
		}
	}
}
