package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// ClockWorker owns only its three loops. Session retains native capabilities,
// the profile lock and journal until Stop has joined and cleanup succeeds.
type ClockWorker struct {
	ctx      context.Context
	cancel   context.CancelFunc
	config   ClockWorkerConfig
	done     chan struct{}
	ready    chan struct{}
	stopGate chan struct{}
	stopped  bool
	disable  func() error
	cleanup  func(context.Context) error
	poll     func(context.Context, time.Duration) (ClockPollResult, error)
	// held reports whether the next poll may hold its native read for
	// config.PollWait; nil holds whenever PollWait is set.
	held       func() bool
	renew      func(context.Context) (ClockRenewResult, error)
	step       func(context.Context, StepReason) (ClockSchedulerResult, error)
	stopParent func() bool
	// wake is the step loop's own signal. The poll loop notifies it and
	// config.Wake alike, so the Worker and the step loop each drain their
	// own pending evidence instead of racing for one channel token.
	wake *WakeSignal
	// pollWake ends the poll loop's cadence sleep when a step leaves a
	// window running, so the held read starts with the window instead of
	// up to a PollInterval later; pollHeld records whether the last read
	// was already held, in which case the loop re-polls on its own.
	pollWake chan struct{}
	pollHeld atomic.Bool
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
	w := &ClockWorker{ctx: lifetime, cancel: cancel, config: config, done: make(chan struct{}), ready: make(chan struct{}), stopGate: make(chan struct{}, 1), disable: scheduler.session.disableClockWorker, cleanup: scheduler.session.CleanupClock, step: scheduler.StepWithReason, renew: scheduler.RenewEpoch, held: scheduler.WindowRunning, wake: NewWakeSignal(), pollWake: make(chan struct{}, 1)}
	w.poll = func(ctx context.Context, wait time.Duration) (ClockPollResult, error) {
		return scheduler.PollEvents(ctx, nativeEvents, config.PageLimit, wait)
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

// Nudge wakes the step loop without evidence: the Worker calls it after a
// step that advanced a plan, so a review deferred on the latched outcome it
// just reconciled runs as soon as the successor is dispatched (issue #162).
func (w *ClockWorker) Nudge() {
	w.wake.Notify(nil, false)
}

// WindowRunning is the scheduler's hint that a window it admitted was still
// running at its last evidence (ClockScheduler.WindowRunning).
func (w *ClockWorker) WindowRunning() bool { return w.held != nil && w.held() }

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

// pollLoop reads the native journal. While held reports a window running
// the read is a long poll bounded by PollWait, so a stop wakes the step as
// soon as its event lands instead of at the next PollInterval, or, with
// PollWait zero, an unheld read at the RunningPollInterval cadence;
// otherwise the read returns at once and the loop keeps the PollInterval
// cadence, which a step that leaves a window running cuts short (pollWake)
// so the held read begins with the window. The read is never held under a
// review, where it would queue ahead of the planners' reads. A call that
// waited (it returned no sooner than half of its wait) or that captured
// evidence is followed by the next poll at once; a call that returned early
// against a native build that ignores wait_ms falls back to the cadence.
func (w *ClockWorker) pollLoop() {
	ready := false
	for w.ctx.Err() == nil {
		var wait time.Duration
		running := w.held == nil || w.held()
		if w.config.PollWait > 0 && running {
			wait = w.config.PollWait
		}
		w.pollHeld.Store(wait > 0)
		interval := w.config.PollInterval
		if running && w.config.RunningPollInterval > 0 {
			interval = w.config.RunningPollInterval
		}
		call, cancel := context.WithTimeout(w.ctx, w.config.PollTimeout)
		started := time.Now()
		result, err := w.poll(call, wait)
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
		waited := wait > 0 && time.Since(started) >= wait/2
		if err == nil && (waited || result.Captured) {
			if w.ctx.Err() != nil {
				return
			}
			continue
		}
		if _, alive := w.waitOrWake(interval, w.pollWake); !alive {
			return
		}
	}
}

// wakePoll ends the poll loop's current cadence sleep once a window is
// running, so its next read is held from the window's start. A loop whose
// last read was already held is left to its own cadence: it re-polls at
// once after a wait, and a build that ignores wait_ms must not be spun by
// every step.
func (w *ClockWorker) wakePoll() {
	if w.config.PollWait <= 0 || w.pollHeld.Load() || (w.held != nil && !w.held()) {
		return
	}
	select {
	case w.pollWake <- struct{}{}:
	default:
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
	request, phase, reasons, failure               string
	failed, running, reconciled, cleaned, deferred bool
}

func clockWorkerKey(result ClockSchedulerResult, err error) clockStepKey {
	key := clockStepKey{failed: err != nil, running: result.Running, reconciled: result.Reconciled, cleaned: result.Cleaned, deferred: result.Deferred}
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
// to try its next pause-bound admission before the next window starts;
// clockPauseDrainTotal bounds the whole hold. Each admission costs the
// Worker one step of native reads, so a backlog of them (an eight-action
// harvest plan, or two) needs a stop that lasts while admissions keep
// landing, not one bounded by the cost of the first (#211).
const (
	clockPauseDrainMax   = 5 * time.Second
	clockPauseDrainTotal = 2 * time.Minute
)

// awaitPauseWork waits until the Worker reports no pause-bound work for the
// latest stop, an admission has not landed for clockPauseDrainMax, the
// whole hold reaches clockPauseDrainTotal or the loop ends; it returns the
// time held.
func (w *ClockWorker) awaitPauseWork() time.Duration {
	started := time.Now()
	idle := time.NewTimer(clockPauseDrainMax)
	defer idle.Stop()
	total := time.NewTimer(clockPauseDrainTotal)
	defer total.Stop()
	for {
		select {
		case <-w.ctx.Done():
		case <-idle.C:
		case <-total.C:
		case <-w.config.Wake.PauseDrained():
		case <-w.config.Wake.PauseProgressed():
			if !idle.Stop() {
				<-idle.C
			}
			idle.Reset(clockPauseDrainMax)
			continue
		}
		return time.Since(started)
	}
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
		w.wakePoll()
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
		// Isolated planner failures do not fail the step (#62), so without
		// this line a planner that errors on every step (colony-2's equip
		// planner never armed anyone) leaves no trace outside debug mode.
		if len(result.PlannerFailures) > 0 && changed {
			fmt.Fprintf(os.Stderr, "[clock-worker] planner failures: %v\n", errors.Join(result.PlannerFailures...))
		}
		// A combat window is short by design and the raid is re-planned
		// between windows, so an unchanged decision does not back off.
		// A deferred step waits on the Worker, not on a backoff either: it
		// steps again on the Worker's advance (Nudge) or a StepInterval later.
		if havePrevious && key == previous && !result.Combat && !result.Deferred {
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
		// A step deferred on work the Worker owes does not step again at
		// once: the Worker is waiting on the player gate this step just
		// released, and an immediate step would take it back for another
		// round of reads that can only defer again. It waits for the
		// Worker's advance instead (issue #162).
		if err == nil && (result.Reconciled || result.Cleaned) && !result.Deferred && !skipped {
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
			// The step that settles a committed stop reviews and admits in
			// the same pass (issue #162), so the Worker's pause-bound
			// admissions are held for before it, not after (#129).
			if _, stopped := w.wake.TakeStop(); stopped {
				held := w.awaitPauseWork()
				if w.ctx.Err() != nil {
					return
				}
				clockSchedulerLog("stop committed: pause-bound admissions held %s before the review", held.Round(time.Millisecond))
			}
		}
	}
}
