package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"reflect"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// StepBudget bounds one clock step's planner waves, each part
// reported on the step's clock_step row beside what the step used.
type StepBudget struct {
	// Reads is the most native round trips a step may spend; zero leaves
	// it unbounded. It is reported, and an overrun is flagged on the row,
	// never enforced mid-read.
	Reads uint64
	// Wall bounds the planner waves: the critical wave still pending at
	// the deadline holds admission naming its planners, and the optional
	// wave's cutoff never passes it. Zero means DefaultStepWall.
	Wall time.Duration
	// NativeWork bounds the native-work window a step lends (the largest
	// NativeWorkTicks its planners ask for); zero means the window's own
	// MaxTicks.
	NativeWork uint32
	// OptionalGrace floors how long after the critical wave returns the
	// step still joins the optional wave: the grace is the critical wave's
	// own duration or this, whichever is longer. Zero means
	// DefaultOptionalGrace.
	OptionalGrace time.Duration
}

// DefaultStepWall is the planner waves' wall budget, counted from the wave's
// start (after the rounds): under the 60 s step call (the cold review
// and layout of a slow 2-vCPU runner take ~12 s and its planners ~20 s each, so
// the former 30 s step and 20 s wall cut the step off at every attempt), so a
// wave that never returns holds admission with its planners named instead of
// failing the step on the context deadline.
const DefaultStepWall = 40 * time.Second

// DefaultOptionalGrace is the least grace the optional wave gets after the
// critical wave returns. A step whose critical wave is quicker than this
// (a warm cache, a fake native) still lets the optional reviews finish;
// one whose census and critical reads take seconds gives them that long.
const DefaultOptionalGrace = time.Second

func (b StepBudget) wall() time.Duration {
	if b.Wall <= 0 {
		return DefaultStepWall
	}
	return b.Wall
}

func (b StepBudget) optionalGrace() time.Duration {
	if b.OptionalGrace <= 0 {
		return DefaultOptionalGrace
	}
	return b.OptionalGrace
}

// plannerWave is one step's planner wave: the group that runs the
// planners, the optional planners' cancellable context, each planner's
// private result and which of them returned before the cutoff. Planners
// write their result into a private ClockSchedulerResult, so an optional
// planner still running after the step has moved on writes nothing the
// step reads; merge copies the results of the planners that made the
// cutoff onto the step's own.
type plannerWave struct {
	group          *plannerGroup
	optional       context.Context
	cancelOptional context.CancelFunc
	mu             sync.Mutex
	results        map[string]*ClockSchedulerResult
	finished       []string
	// reasons is each returned planner's reason (its run's, or for a
	// proposal planner its proposal's outcome): what the due queue reads
	// to tell a planner waiting on open work from one that is due.
	reasons map[string]Verdict
	// goals is the goal each queued planner files its verdict on.
	concerns map[string]policy.ConcernID
	// critical records the class each planner was queued under, which is
	// the entry's own class or a startup promotion of it.
	critical map[string]bool
	closed   bool
	// began and took are each planner's queue time and, once it returns,
	// its wall time: the per-planner cost the clock_step row reports.
	began map[string]time.Time
	took  map[string]time.Duration
}

func newPlannerWave(call context.Context) *plannerWave {
	optional, cancel := context.WithCancel(call)
	return &plannerWave{group: newPlannerGroup(call, plannerWidth), optional: optional, cancelOptional: cancel, results: map[string]*ClockSchedulerResult{}, reasons: map[string]Verdict{}, concerns: map[string]policy.ConcernID{}, critical: map[string]bool{}, began: map[string]time.Time{}, took: map[string]time.Duration{}}
}

// queue queues entry's run on the wave: a critical planner under the step
// context, an optional one under the wave's cancellable context.
func (w *plannerWave) queue(s *ClockScheduler, call, epoch context.Context, arbiter *stepArbiter, entry plannerEntry) {
	private := &ClockSchedulerResult{}
	w.results[entry.name] = private
	w.critical[entry.name] = entry.class == classCritical
	w.concerns[entry.name] = entry.concern
	ctx := call
	if entry.class != classCritical {
		ctx = w.optional
	}
	ctx = entry.auditReads(ctx)
	start := time.Now()
	w.mu.Lock()
	w.began[entry.name] = start
	w.mu.Unlock()
	w.group.Go(entry.name, entry.class, entry.priority, func() error {
		var reason Verdict
		run := s.config.Faults.plannerFault(entry.name, ctx, func() (err error) {
			reason, err = entry.run(s, ctx, epoch, private, arbiter)
			return err
		})
		err := run()
		if errors.Is(err, store.ErrStaleOwner) {
			// Planners share a standard (foodAcquisition and fields), so a
			// sibling's admission in the same wave moves the owner under a
			// slow planner; it waits for the next round, not a failure.
			reason, err = awaitingPlan("owner", "revision_changed"), nil
		}
		if errors.Is(err, store.ErrOpenMethod) {
			reason, err = BuildingReasonExistingWork, nil
		}
		took := time.Since(start)
		w.mu.Lock()
		w.took[entry.name] = took
		late := w.closed
		w.mu.Unlock()
		decision := plannerStepDecision(entry, reason, err, took, late)
		if immediateReview(ctx) {
			decision.Attrs["scope"] = "immediate"
			if validity, known := domain.ReadValidityFrom(ctx); known {
				decision.Attrs["decision_tick"] = int64(validity.Tick)
			}
		}
		telemetry.Decide(ctx, decision)
		if err != nil {
			err = fmt.Errorf("%s: %w", entry.name, err)
		}
		return w.done(entry.name, reason, err)
	})
}

// plannerStepDecision is the planner_step row of one planner run: the planner
// (target), its verdict word and cause, wall time, concern, class and, for a
// failure, the error. A run that returned after the cutoff is marked late.
// A refused or waiting verdict's reason is its cause (the subject rides
// in attrs); any other outcome beyond admitted reads as ok with
// the outcome as the reason; a run with no verdict reads ok/no_verdict.
func plannerStepDecision(entry plannerEntry, v Verdict, err error, took time.Duration, late bool) telemetry.Decision {
	if late && entry.class == classOptional && errors.Is(err, context.Canceled) {
		v, err = awaitingPlan("planner", "cutoff"), nil
	}
	d := telemetry.Decision{Kind: "planner_step", Component: "clock-scheduler", Target: entry.name, Dur: took,
		Attrs: map[string]any{"concern": string(entry.concern), "class": string(entry.class)}}
	switch {
	case err != nil:
		d.Level, d.Verdict, d.Reason = slog.LevelWarn, "failed", "error"
		d.Attrs["error"] = err
	case v.IsZero():
		d.Verdict, d.Reason = "ok", "no_verdict"
	case v.Outcome == OutcomeAdmitted || v.Outcome == OutcomeWaiting || v.Outcome == OutcomeRefused:
		d.Verdict, d.Reason = string(v.Outcome), string(v.Refusal.Kind)
		if v.Outcome == OutcomeAdmitted {
			d.Reason = "plan_admitted"
		}
	default:
		d.Verdict, d.Reason = "ok", string(v.Outcome)
	}
	if v.Refusal.Subject != "" {
		d.Attrs["subject"] = v.Refusal.Subject
	}
	if late {
		d.Attrs["late"] = true
	}
	return d
}

// done records a planner's return and its reason. After the cutoff the
// return is discarded: the planner was recorded missed, its result is not
// merged and its (cancellation) error is not a failure.
func (w *plannerWave) done(name string, reason Verdict, err error) error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return nil
	}
	w.finished = append(w.finished, name)
	if err == nil {
		w.reasons[name] = reason
	}
	return err
}

// decided records a reason decided after the planner returned: a
// proposal planner's proposal outcome from the coordinator.
func (w *plannerWave) decided(name string, reason Verdict) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.reasons[name] = reason
}

// reason is the reason recorded for name; false for a planner that failed
// or missed the cutoff.
func (w *plannerWave) reason(name string) (Verdict, bool) {
	w.mu.Lock()
	defer w.mu.Unlock()
	reason, ok := w.reasons[name]
	return reason, ok
}

// filing is the goal and verdict a returned planner files on: the goal comes
// with the result. False for a planner that serves no single goal, failed or
// missed the cutoff.
func (w *plannerWave) filing(name string) (policy.ConcernID, Verdict, bool) {
	w.mu.Lock()
	defer w.mu.Unlock()
	verdict, ok := w.reasons[name]
	goal := w.concerns[name]
	return goal, verdict, ok && goal != ""
}

// queuedCritical reports whether the named planner was queued into this
// wave's critical cycle, promotion included: a pending one holds admission
// instead of being recorded as having missed the cutoff.
func (w *plannerWave) queuedCritical(name string) bool {
	w.mu.Lock()
	defer w.mu.Unlock()
	return w.critical[name]
}

// finishedNames lists the planners that returned before the cutoff, in
// return order.
func (w *plannerWave) finishedNames() []string {
	w.mu.Lock()
	defer w.mu.Unlock()
	return append([]string(nil), w.finished...)
}

// close ends the wave at the cutoff: the optional planners still running
// are cancelled and their later returns discarded. It returns the names
// still pending, in queue order.
func (w *plannerWave) close() []string {
	w.mu.Lock()
	pending := w.group.Pending()
	w.closed = true
	w.mu.Unlock()
	w.cancelOptional()
	return pending
}

// merge copies the results of every planner that returned before the
// cutoff onto out: each planner's private result holds its own pointer
// field alone.
// plannerMS is each queued planner's wall time in milliseconds since it was
// queued: its full run once it returned, the elapsed time so far for one
// still evaluating (a missed cutoff).
func (w *plannerWave) plannerMS() map[string]float64 {
	w.mu.Lock()
	defer w.mu.Unlock()
	out := make(map[string]float64, len(w.began))
	for name, start := range w.began {
		d, ok := w.took[name]
		if !ok {
			d = time.Since(start)
		}
		out[name] = float64(d) / float64(time.Millisecond)
	}
	return out
}

func (w *plannerWave) merge(out *ClockSchedulerResult) {
	w.mu.Lock()
	defer w.mu.Unlock()
	target := reflect.ValueOf(out).Elem()
	for _, name := range w.finished {
		source := reflect.ValueOf(w.results[name]).Elem()
		for i := 0; i < source.NumField(); i++ {
			field := source.Field(i)
			if field.Kind() == reflect.Ptr && !field.IsNil() {
				target.Field(i).Set(field)
			}
		}
	}
}

// after is a channel closed once d has passed (never, for d <= 0 it is
// closed at once).
func after(d time.Duration) <-chan struct{} {
	done := make(chan struct{})
	if d <= 0 {
		close(done)
		return done
	}
	time.AfterFunc(d, func() { close(done) })
	return done
}
