package buildingruntime

import (
	"context"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Committed poll evidence wakes the step loop out of its backoff at once.
func TestClockWorkerWakesStepOnCapturedEvents(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	w.config.StepInterval = 20 * time.Millisecond
	w.config.MaxBackoff = 2 * time.Second
	w.config.PollInterval = 2 * time.Second
	w.config.Wake = NewWakeSignal()
	var steps atomic.Int32
	w.step = func(context.Context) (ClockSchedulerResult, error) { steps.Add(1); return ClockSchedulerResult{}, nil }
	var polls atomic.Int32
	w.poll = func(context.Context) (ClockPollResult, error) {
		if polls.Add(1) == 3 {
			return ClockPollResult{Captured: true, Wake: []WakeOutcome{{Action: "wall", Attempt: 1, Terminal: true}}}, nil
		}
		return ClockPollResult{}, nil
	}
	w.start()
	// Two unchanged steps push the backoff to 80ms; wait until the loop is
	// inside that longer sleep.
	deadline := time.Now().Add(time.Second)
	for steps.Load() < 3 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	before := steps.Load()
	w.config.Wake.Notify([]WakeOutcome{{Action: "wall", Attempt: 1, Terminal: true}}, false)
	time.Sleep(15 * time.Millisecond)
	if steps.Load() != before+1 {
		t.Fatal("wake did not step at once", before, steps.Load())
	}
	outcomes, _ := w.config.Wake.Take()
	if outcomes["wall"].Attempt != 1 {
		t.Fatal(outcomes)
	}
}

// A native build that ignores wait_ms returns at once: the loop keeps the
// PollInterval cadence instead of spinning; one that waited re-polls at once.
func TestClockWorkerLongPollCadence(t *testing.T) {
	t.Parallel()
	for _, waits := range []bool{false, true} {
		w := clockLoopFixture(t)
		w.config.PollInterval = 30 * time.Millisecond
		w.config.PollWait = 10 * time.Millisecond
		var polls atomic.Int32
		w.poll = func(ctx context.Context) (ClockPollResult, error) {
			polls.Add(1)
			if waits {
				time.Sleep(w.config.PollWait)
			}
			return ClockPollResult{}, nil
		}
		w.start()
		time.Sleep(100 * time.Millisecond)
		n := polls.Load()
		if !waits && (n < 2 || n > 5) || waits && n < 7 {
			t.Fatal(waits, n)
		}
	}
}

func TestWakeSignalMergesAndDrains(t *testing.T) {
	t.Parallel()
	var none *WakeSignal
	none.Notify([]WakeOutcome{{Action: "a"}}, true)
	if o, a := none.Take(); o != nil || a || none.C() != nil {
		t.Fatal("nil signal must be inert")
	}
	w := NewWakeSignal()
	w.Notify([]WakeOutcome{{Action: "a", Attempt: 1}}, false)
	w.Notify([]WakeOutcome{{Action: "a", Attempt: 2, Terminal: true}, {Action: "b", Attempt: 1}}, true)
	select {
	case <-w.C():
	default:
		t.Fatal("no wake")
	}
	select {
	case <-w.C():
		t.Fatal("wake channel must coalesce")
	default:
	}
	outcomes, authority := w.Take()
	if !authority || len(outcomes) != 2 || outcomes[domain.ActionID("a")].Attempt != 2 || !outcomes["a"].Terminal {
		t.Fatal(outcomes, authority)
	}
	if o, a := w.Take(); len(o) != 0 || a {
		t.Fatal("drain must empty")
	}
}
