package buildingruntime

import (
	"context"
	"fmt"
	"reflect"
	"sync"
	"time"
)

// StepBudget bounds one clock step's planner waves (#623), each part
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

// DefaultStepWall is the planner waves' wall budget: under the 30 s step
// call so a wave that never returns holds admission with its planners
// named instead of failing the step on the context deadline.
const DefaultStepWall = 20 * time.Second

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

// plannerWave is one step's planner wave (#623): the group that runs the
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
	closed         bool
}

func newPlannerWave(call context.Context) *plannerWave {
	optional, cancel := context.WithCancel(call)
	return &plannerWave{group: newPlannerGroup(call, plannerWidth), optional: optional, cancelOptional: cancel, results: map[string]*ClockSchedulerResult{}}
}

// queue queues entry's run on the wave: a critical planner under the step
// context, an optional one under the wave's cancellable context.
func (w *plannerWave) queue(s *ClockScheduler, call, epoch context.Context, arbiter *stepArbiter, entry plannerEntry) {
	private := &ClockSchedulerResult{}
	w.results[entry.name] = private
	ctx := call
	if entry.class != classCritical {
		ctx = w.optional
	}
	w.group.Go(entry.name, entry.class, entry.priority, func() error {
		err := entry.run(s, ctx, epoch, private, arbiter)
		if err != nil {
			err = fmt.Errorf("%s: %w", entry.name, err)
		}
		return w.done(entry.name, err)
	})
}

// done records a planner's return. After the cutoff the return is
// discarded: the planner was recorded missed, its result is not merged and
// its (cancellation) error is not a failure.
func (w *plannerWave) done(name string, err error) error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return nil
	}
	w.finished = append(w.finished, name)
	return err
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
