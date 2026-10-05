package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
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
	poll     func(context.Context) (ClockPollResult, error)
	// held reports a window running (the running poll cadence); nil
	// counts as running.
	held       func() bool
	trace      func() telemetry.Trace
	validity   func() (domain.ReadValidity, bool)
	renew      func(context.Context) (ClockRenewResult, error)
	step       func(context.Context, StepReason) (ClockSchedulerResult, error)
	stopParent func() bool
	// wake is the step loop's own signal. The poll loop notifies it and
	// config.Wake alike, so the Worker and the step loop each drain their
	// own pending evidence instead of racing for one channel token.
	wake *WakeSignal
	// pollWake releases the local between-window wait after each step,
	// and starts a running window's poll without a cadence delay.
	pollWake chan struct{}
	// signal is the clock channel's announcements (rimgovernor.clock); nil
	// when the native has none, which leaves the cadence poll.
	signal *bridge.ClockSignal
}

// Mirror poll transport-error backoff (#795): from pollBackoffMin,
// doubling to pollBackoffMax, reset by a successful poll.
const (
	pollBackoffMin = 250 * time.Millisecond
	pollBackoffMax = 2 * time.Second
)

func NewClockWorker(ctx context.Context, scheduler *ClockScheduler, nativeEvents ClockEventNative, config ClockWorkerConfig) (*ClockWorker, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if scheduler == nil || nativeEvents == nil {
		return nil, fmt.Errorf("%w: NewClockWorker: scheduler == nil || nativeEvents == nil", ErrControl)
	}
	// The authority Mode has no time-based expiry, so the poll and renew
	// cadence is bounded only by the clock's own requested lease duration.
	// The step loop never touches the lease: Step holds the Player gate, not
	// renewGate, so a slow step cannot delay RenewEpoch, and its timeout is
	// bounded by the Player's call timeout alone.
	lease := time.Duration(scheduler.config.Start.LeaseMS) * time.Millisecond
	playerTimeout := scheduler.player.config.CallTimeout
	if config.PollInterval <= 0 || config.RenewInterval <= 0 || config.StepInterval <= 0 || config.PollInterval > lease/4 || config.RenewInterval > lease/4 || config.MaxBackoff < config.StepInterval || config.MaxBackoff > time.Minute || config.PageLimit < 1 || config.PageLimit > 128 {
		return nil, fmt.Errorf("%w: NewClockWorker: config.PollInterval <= 0 || config.RenewInterval <= 0 || config.StepInterval <= 0 || config.PollInterval >", ErrControl)
	}
	if config.PollTimeout <= 0 || config.PollTimeout > lease/4 || config.PollTimeout > playerTimeout || config.RenewTimeout <= 0 || config.RenewTimeout > lease/4 || config.RenewTimeout > playerTimeout || config.StepTimeout <= 0 || config.StepTimeout > playerTimeout {
		return nil, fmt.Errorf("%w: NewClockWorker: config.PollTimeout <= 0 || config.PollTimeout > lease/4 || config.PollTimeout > playerTimeout || config.Ren", ErrControl)
	}
	// A missed announcement is found by the next read; bound how long that waits.
	if config.SignalWait < 0 || config.SignalWait > ClockSignalWaitMax {
		return nil, fmt.Errorf("%w: NewClockWorker: config.SignalWait < 0 || config.SignalWait > ClockSignalWaitMax", ErrControl)
	}
	lifetime, cancel := context.WithCancel(ctx)
	w := &ClockWorker{ctx: lifetime, cancel: cancel, config: config, done: make(chan struct{}), ready: make(chan struct{}), stopGate: make(chan struct{}, 1), disable: scheduler.session.disableClockWorker, cleanup: scheduler.session.CleanupClock, step: scheduler.StepWithReason, renew: scheduler.RenewEpoch, held: scheduler.WindowRunning, trace: scheduler.Trace, validity: scheduler.Validity, wake: NewWakeSignal(), pollWake: make(chan struct{}, 1)}
	w.poll = func(ctx context.Context) (ClockPollResult, error) {
		return scheduler.PollEvents(ctx, nativeEvents, config.PageLimit)
	}
	if source, ok := nativeEvents.(clockSignalSource); ok {
		w.signal = source.ClockSignal()
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

// Trace is the trace of the scheduler's latest step (ClockScheduler.Trace).
func (w *ClockWorker) Trace() telemetry.Trace { return w.trace() }

// Validity is the read validity of the scheduler's latest step
// (ClockScheduler.Validity, #624).
func (w *ClockWorker) Validity() (domain.ReadValidity, bool) {
	if w.validity == nil {
		return domain.ReadValidity{}, false
	}
	return w.validity()
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

// pollLoop reads the native journal through clock_read_events (#858): one
// unheld read per round, answered at once. With the clock channel's signal
// (SignalWait > 0) the loop reads again when the mod announces a journal
// advance on rimgovernor.clock, so a stop is seen near-push, or after
// SignalWait at the latest (a bound, not a held call: nothing waits in
// native). The version is taken before the read so an announcement that
// lands during it still releases the next wait, and a (re)subscription moves
// it, so a reconnect always ends in a tail read of the journal. A read that
// captured evidence is followed by the next at once. Without the signal the
// loop reads at the PollInterval cadence (RunningPollInterval while a window
// runs); between windows it waits locally on scheduler completion
// (pollWake). A failed call backs off from 250 ms to 2 s.
func (w *ClockWorker) pollLoop() {
	ready := false
	backoff := time.Duration(0)
	signalled := w.signal != nil && w.config.SignalWait > 0
	for w.ctx.Err() == nil {
		running := w.held == nil || w.held()
		interval := w.config.PollInterval
		if running && w.config.RunningPollInterval > 0 {
			interval = w.config.RunningPollInterval
		}
		var seen uint64
		if signalled {
			seen = w.signal.Version()
		}
		call, cancel := context.WithTimeout(w.ctx, w.config.PollTimeout)
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
			w.wake.NotifySections(result.Wake, result.Invalidated, result.InvalidatedSections, result.AuthorityChanged, result.Stopped, result.StoppedAt)
		}
		if err == nil && result.Captured {
			backoff = 0
			continue
		}
		if err != nil {
			backoff = min(pollBackoffMax, max(pollBackoffMin, backoff*2))
			interval = backoff
		} else {
			backoff = 0
			if signalled {
				w.signal.Wait(w.ctx, seen, w.config.SignalWait)
				continue
			}
		}
		// Step completion releases this wait immediately. The cadence is
		// still a safety bound: a blocked step must not hide player input or
		// an authority interruption from the independent poll loop.
		if _, alive := w.waitOrWake(interval, w.pollWake); !alive {
			return
		}
	}
}

// wakePoll releases the between-window wait on every completed step, and
// ends the cadence sleep when a window starts. A running loop is left to its
// own cadence or clock-channel signal.
func (w *ClockWorker) wakePoll() {
	if w.held == nil || w.held() {
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

// clockWorkerStepEvent publishes one planner_step row (target
// bridge.WorkerStepTarget) for the step: failed on its error (WARN) or on the
// planner failures it isolated, waiting on a hold or retry, else admitted or
// waiting on the window; the failures and outcome flags ride in attrs, with
// how many unlogged steps restated the previous outcome.
func clockWorkerStepEvent(ctx context.Context, result ClockSchedulerResult, err error, repeats int) {
	level, verdict, reason := slog.LevelInfo, "waiting", "no_window"
	if result.Decision.Admitted {
		verdict, reason = "admitted", "window_admitted"
	}
	failures := make([]string, 0, len(result.PlannerFailures))
	var unselected []string
	for _, failure := range result.PlannerFailures {
		// A goal the ranking left without a development slot this round is
		// waiting its turn, not faulting.
		if errors.Is(failure, store.ErrNotAdmitted) {
			unselected = append(unselected, failure.Error())
			continue
		}
		failures = append(failures, failure.Error())
	}
	if len(failures) > 0 {
		verdict, reason = "failed", "planner_failures"
	}
	// A bare hold (no planner failed, nothing else joined) is the step
	// waiting, not failing: the player log keeps its Warn for real faults.
	if err != nil {
		level, verdict, reason = slog.LevelWarn, "failed", "step_error"
		if err == executor.ErrHeld && len(failures) == 0 {
			level, verdict, reason = slog.LevelInfo, "waiting", "held"
		} else if (errors.Is(err, executor.ErrAuthority) || errors.Is(err, observation.ErrChanged) || errors.Is(err, ErrControl)) && len(failures) == 0 {
			// Authority, the native observation or the planner read lapsed between the step's
			// state read and its review (a poll hold, a resume, a reload in
			// flight): the next step reviews with fresh state or exits on
			// authority's absence, so it is a retry, not a fault.
			level, verdict, reason = slog.LevelInfo, "waiting", "retry"
		} else if strings.Contains(err.Error(), "FAILURE_CODE_OWNER_CONFLICT") && len(failures) == 0 {
			// A restarted controller asking after its old clock epoch: it opens a
			// new one on the next step.
			level, verdict, reason = slog.LevelInfo, "waiting", "epoch_reopen"
		}
	}
	proposals := make([]string, 0, len(result.Proposals))
	for _, outcome := range result.Proposals {
		switch {
		case outcome.Admitted:
			proposals = append(proposals, outcome.Proposal+" admitted")
		case len(outcome.Demand) != 0:
			proposals = append(proposals, fmt.Sprintf("%s %s %s demand %v", outcome.Proposal, outcome.Verdict, outcome.Waiting, outcome.Demand))
		default:
			proposals = append(proposals, outcome.Proposal+" "+outcome.Verdict.String()+" "+outcome.Waiting)
		}
	}
	// The readers (spectator now, phases, step stall) use admitted, running,
	// window_ticks and planner_failures; every other field is written only
	// when it says something.
	attrs := map[string]any{"planner_failures": failures, "admitted": result.Decision.Admitted, "running": result.Running, "window_ticks": result.Window.Ticks}
	if err != nil {
		attrs["error"] = err
	}
	if len(unselected) > 0 {
		attrs["planner_unselected"] = unselected
	}
	if len(proposals) > 0 {
		attrs["proposals"] = proposals
	}
	if result.Reason.Cause != "" {
		attrs["cause"] = string(result.Reason.Cause)
	}
	for _, flag := range []struct {
		name string
		set  bool
	}{{"reconciled", result.Reconciled}, {"cleaned", result.Cleaned}, {"deferred", result.Deferred}, {"retaken", result.Retaken}, {"combat", result.Combat}} {
		if flag.set {
			attrs[flag.name] = true
		}
	}
	if repeats > 0 {
		attrs["repeated"] = repeats
	}
	telemetry.Decide(ctx, telemetry.Decision{Kind: "planner_step", Component: "clock-worker", Level: level, Verdict: verdict, Reason: reason, Target: bridge.WorkerStepTarget, Attrs: attrs})
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
		// The loop mints the step's trace so the planner_step row below
		// shares it with every row the step wrote (#298).
		call, cancel := context.WithTimeout(telemetry.WithTrace(w.ctx, telemetry.NewTrace()), w.config.StepTimeout)
		result, err := w.step(call, reason)
		cancel()
		w.wakePoll()
		key := clockWorkerKey(result, err)
		changed := !havePrevious || key != previous
		// One planner_step row per change of outcome (like the backoff
		// decision below), so a sustained failure logs once, not every
		// StepInterval, and the next change carries the repeat count.
		// stepPlanners wraps each planner's error with its own name
		// (clock_scheduler.go), so the failure is diagnosable from this
		// line alone; the stall diagnosis reads it (issues #45, #100).
		if changed {
			clockWorkerStepEvent(call, result, err, repeats)
			repeats = 0
		} else {
			repeats++
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
