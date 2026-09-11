package buildingruntime

import (
	"context"
	"errors"
	"strings"
	"sync"
	"time"
)

// ClockWorker owns only its three loops. Session retains native capabilities,
// the profile lock and journal until Stop has joined and cleanup succeeds.
type ClockWorker struct {
	ctx         context.Context
	cancel      context.CancelFunc
	config      ClockWorkerConfig
	done        chan struct{}
	ready       chan struct{}
	disable     func() error
	cleanup     func(context.Context) error
	poll        func(context.Context) (ClockPollResult, error)
	renew       func(context.Context) (ClockRenewResult, error)
	step        func(context.Context) (ClockSchedulerResult, error)
	stopParent  func() bool
	stopContext func() bool
}

func NewClockWorker(ctx context.Context, scheduler *ClockScheduler, nativeEvents ClockEventNative, config ClockWorkerConfig) (*ClockWorker, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if scheduler == nil || nativeEvents == nil {
		return nil, ErrControl
	}
	lease := min(time.Duration(scheduler.config.Start.LeaseMS)*time.Millisecond, scheduler.session.control.config.LeaseDuration)
	if config.PollInterval <= 0 || config.RenewInterval <= 0 || config.StepInterval <= 0 || config.CallTimeout <= 0 || config.PollInterval > lease/4 || config.RenewInterval > lease/4 || config.CallTimeout > lease/4 || config.CallTimeout > scheduler.player.config.CallTimeout || config.MaxBackoff < config.StepInterval || config.MaxBackoff > time.Minute || config.PageLimit < 1 || config.PageLimit > 128 {
		return nil, ErrControl
	}
	lifetime, cancel := context.WithCancel(ctx)
	w := &ClockWorker{ctx: lifetime, cancel: cancel, config: config, done: make(chan struct{}), ready: make(chan struct{}), disable: scheduler.session.disableClockWorker, cleanup: scheduler.session.CleanupClock, step: scheduler.Step, renew: scheduler.RenewEpoch}
	w.poll = func(ctx context.Context) (ClockPollResult, error) {
		return scheduler.PollEvents(ctx, nativeEvents, config.PageLimit)
	}
	if err := scheduler.session.attachClockWorker(w); err != nil {
		cancel()
		close(w.done)
		return nil, err
	}
	w.stopParent = context.AfterFunc(scheduler.player.lifetime, func() { cancel(); _ = w.disable() })
	w.stopContext = context.AfterFunc(lifetime, func() { _ = w.disable() })
	w.start()
	return w, nil
}

func (w *ClockWorker) start() {
	var joined sync.WaitGroup
	joined.Add(3)
	go func() { defer joined.Done(); w.pollLoop() }()
	go func() { defer joined.Done(); w.renewLoop() }()
	go func() { defer joined.Done(); w.stepLoop() }()
	go func() { joined.Wait(); close(w.done) }()
}

// Stop can be retried after a deadline or cleanup failure. It does not take the
// Player gate or close Player, and never releases a resource still used by a loop.
func (w *ClockWorker) Stop(ctx context.Context) error {
	w.cancel()
	disabled := w.disable()
	select {
	case <-w.done:
	case <-ctx.Done():
		return errors.Join(disabled, ctx.Err())
	}
	if w.stopParent != nil {
		w.stopParent()
	}
	if w.stopContext != nil {
		w.stopContext()
	}
	return errors.Join(disabled, w.cleanup(ctx))
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
	request, phase, reasons              string
	failed, running, reconciled, cleaned bool
}

func clockWorkerKey(result ClockSchedulerResult, err error) clockStepKey {
	key := clockStepKey{failed: err != nil, running: result.Running, reconciled: result.Reconciled, cleaned: result.Cleaned}
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
