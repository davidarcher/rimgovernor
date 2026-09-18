package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
	step       func(context.Context, StepReason) (ClockSchedulerResult, error)
	stopParent func() bool
	// wake is the step loop's own signal. The poll loop notifies it and
	// config.Wake alike, so the Worker and the step loop each drain their
	// own pending evidence instead of racing for one channel token.
	wake *WakeSignal
}

func NewClockWorker(ctx context.Context, scheduler *ClockScheduler, nativeEvents ClockEventNative, config ClockWorkerConfig) (*ClockWorker, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if scheduler == nil || nativeEvents == nil {
		return nil, ErrControl
	}
	// The authority Mode has no time-based expiry, so the poll and renew
	// cadence is bounded only by the clock's own requested lease duration.
	// The step loop never touches the lease: Step holds the Player gate, not
	// renewGate, so a slow step cannot delay RenewEpoch, and its timeout is
	// bounded by the Player's call timeout alone.
	lease := time.Duration(scheduler.config.Start.LeaseMS) * time.Millisecond
	playerTimeout := scheduler.player.config.CallTimeout
	if config.PollInterval <= 0 || config.RenewInterval <= 0 || config.StepInterval <= 0 || config.PollInterval > lease/4 || config.RenewInterval > lease/4 || config.MaxBackoff < config.StepInterval || config.MaxBackoff > time.Minute || config.PageLimit < 1 || config.PageLimit > 128 {
		return nil, ErrControl
	}
	if config.PollTimeout <= 0 || config.PollTimeout > lease/4 || config.PollTimeout > playerTimeout || config.RenewTimeout <= 0 || config.RenewTimeout > lease/4 || config.RenewTimeout > playerTimeout || config.StepTimeout <= 0 || config.StepTimeout > playerTimeout {
		return nil, ErrControl
	}
	// A long poll must still leave the read itself a second under its timeout.
	if config.PollWait < 0 || config.PollWait > bridge.ClockEventsMaxWaitMs*time.Millisecond || (config.PollWait > 0 && config.PollWait+time.Second > config.PollTimeout) {
		return nil, ErrControl
	}
	lifetime, cancel := context.WithCancel(ctx)
	w := &ClockWorker{ctx: lifetime, cancel: cancel, config: config, done: make(chan struct{}), ready: make(chan struct{}), stopGate: make(chan struct{}, 1), disable: scheduler.session.disableClockWorker, cleanup: scheduler.session.CleanupClock, step: scheduler.StepWithReason, renew: scheduler.RenewEpoch, wake: NewWakeSignal()}
	w.poll = func(ctx context.Context) (ClockPollResult, error) {
		return scheduler.PollEvents(ctx, nativeEvents, config.PageLimit, config.PollWait)
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
	woken, alive := w.waitOrWake(delay, nil)
	return alive && !woken
}

// waitOrWake sleeps for delay unless the wake signal fires first. It reports
// whether the wake fired and whether the worker is still alive.
func (w *ClockWorker) waitOrWake(delay time.Duration, wake <-chan struct{}) (woken, alive bool) {
	timer := time.NewTimer(delay)
	defer timer.Stop()
	select {
	case <-w.ctx.Done():
		return false, false
	case <-timer.C:
		return false, w.ctx.Err() == nil
	case <-wake:
		return true, w.ctx.Err() == nil
	}
}

// pollLoop long-polls the native journal. A call that waited (it returned
// no sooner than half of PollWait) or that captured evidence is followed by
// the next poll at once; a call that returned early against a native build
// that ignores wait_ms falls back to the PollInterval cadence.
func (w *ClockWorker) pollLoop() {
	ready := false
	for w.ctx.Err() == nil {
		call, cancel := context.WithTimeout(w.ctx, w.config.PollTimeout)
		started := time.Now()
		result, err := w.poll(call)
		cancel()
		if err == nil && !ready && w.ctx.Err() == nil {
			close(w.ready)
			ready = true
		}
		if err == nil && (result.Captured || len(result.Wake) > 0 || len(result.Invalidated) > 0 || result.AuthorityChanged) {
			// The Worker alone acts on a stop: the step loop reacts to the
			// same page by settling the epoch, and the stop is not a
			// decision input of its own; its step carries the stop only
			// to publish the stop-to-step latency.
			w.config.Wake.NotifyStopped(result.Wake, result.Invalidated, result.AuthorityChanged, result.Stopped)
			w.wake.NotifyStopAt(result.Wake, result.Invalidated, result.AuthorityChanged, result.Stopped, result.StoppedAt)
		}
		waited := w.config.PollWait > 0 && time.Since(started) >= w.config.PollWait/2
		if err == nil && (waited || result.Captured) {
			if w.ctx.Err() != nil {
				return
			}
			continue
		}
		if !w.wait(w.config.PollInterval) {
			return
		}
	}
}
func (w *ClockWorker) renewLoop() {
	for w.wait(w.config.RenewInterval) {
		call, cancel := context.WithTimeout(w.ctx, w.config.RenewTimeout)
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
	if len(result.PlannerFailures) > 0 {
		// Isolated planner failures do not fail the step (#62) but are still a
		// state change: a changed set logs once more.
		key.failure += "; " + errors.Join(result.PlannerFailures...).Error()
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

// clockPauseDrainMax bounds how long a settled epoch waits for the Worker
// to try its pause-bound admissions before the next window starts.
const clockPauseDrainMax = 5 * time.Second

// awaitPauseWork waits until the Worker reports no pause-bound work for the
// latest stop, the bound elapses or the loop ends; it returns the time held.
func (w *ClockWorker) awaitPauseWork() time.Duration {
	started := time.Now()
	timer := time.NewTimer(clockPauseDrainMax)
	defer timer.Stop()
	select {
	case <-w.ctx.Done():
	case <-timer.C:
	case <-w.config.Wake.PauseDrained():
	}
	return time.Since(started)
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
	repeats := 0
	skipped := false
	// The first step plans everything; each later step's reason is what
	// ended the wait before it: the timer, a wake, or a settled epoch.
	reason := StepReason{Cause: StepFull}
	for w.ctx.Err() == nil {
		call, cancel := context.WithTimeout(w.ctx, w.config.StepTimeout)
		result, err := w.step(call, reason)
		cancel()
		key := clockWorkerKey(result, err)
		changed := !havePrevious || key != previous
		if changed {
			clockSchedulerLog("step done: err=%v planner failures=%v%s", err, errors.Join(result.PlannerFailures...), workerRepeats(repeats))
			repeats = 0
		} else {
			repeats++
		}
		// Unconditionally surface which planner failed and why -- stepPlanners
		// wraps each planner's error with its own name (clock_scheduler.go), so
		// this is diagnosable without RIMGOVERNOR_CLOCK_DEBUG=1. Gated on state
		// change (like the backoff decision below) so a sustained failure logs
		// once, not every StepInterval; the debug line above carries the
		// repeat count. See issues #45 and #100.
		if err != nil && changed {
			fmt.Fprintf(os.Stderr, "[clock-worker] step failed: %v\n", err)
		}
		// A combat window is short by design and the raid is re-planned
		// between windows, so an unchanged decision does not back off.
		if havePrevious && key == previous && !result.Combat {
			delay = min(w.config.MaxBackoff, delay*2)
		} else {
			delay = w.config.StepInterval
		}
		previous, havePrevious = key, true
		// A step that only reconciled or cleaned up an epoch (a window that
		// stopped on its tick budget, or a dispatch to reconcile) has left
		// the planners for the next step: run it now rather than idle a
		// StepInterval with the game paused (issue #91). Never twice in a
		// row: a cleanup that keeps succeeding without settling is a
		// backoff case, not a hot loop.
		if err == nil && (result.Reconciled || result.Cleaned) && !skipped {
			skipped = true
			// The game is paused between windows and that is the only
			// moment the Worker can make a pause-bound admission
			// (excavation, acquisition, bed assignment): hold the next
			// window until it has tried each one, within a bound (#129).
			held := w.awaitPauseWork()
			if w.ctx.Err() != nil {
				return
			}
			clockSchedulerLog("step settled an epoch (reconciled=%v cleaned=%v): stepping again at once (pause-bound admissions held %s)", result.Reconciled, result.Cleaned, held.Round(time.Millisecond))
			reason = StepReason{Cause: StepSettled}
			continue
		}
		skipped = false
		// Committed clock evidence (a latched outcome, an authority change)
		// wakes the step at once and resets the backoff: the decision inputs
		// changed, so the unchanged-key backoff no longer applies.
		woken, alive := w.waitOrWake(delay, w.wake.C())
		if !alive {
			return
		}
		reason = StepReason{Cause: StepTimer}
		if woken {
			delay = w.config.StepInterval
			havePrevious = false
			reason = w.wake.TakeInvalidated()
		}
	}
}
