package buildingruntime

import (
	"context"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Committed poll evidence wakes the step loop out of its backoff at once,
// carrying the evidence as the step's reason; the shared Worker signal is
// notified too, and keeps its own pending set. The first step is full and
// timer steps say so.
func TestClockWorkerWakesStepOnCapturedEvents(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	w.config.StepInterval = 20 * time.Millisecond
	w.config.MaxBackoff = 2 * time.Second
	w.config.Wake = NewWakeSignal()
	var steps atomic.Int32
	var mu sync.Mutex
	var reasons []StepReason
	w.step = func(_ context.Context, reason StepReason) (ClockSchedulerResult, error) {
		mu.Lock()
		reasons = append(reasons, reason)
		mu.Unlock()
		steps.Add(1)
		return ClockSchedulerResult{}, nil
	}
	polled := make(chan struct{})
	var polls atomic.Int32
	w.poll = func(context.Context, time.Duration) (ClockPollResult, error) {
		if polls.Add(1) != 2 {
			return ClockPollResult{}, nil
		}
		<-polled
		return ClockPollResult{Captured: true, Wake: []WakeOutcome{{Action: "wall", Attempt: 1, Terminal: true}}, Invalidated: []bridge.FactFamily{bridge.FactRooms}}, nil
	}
	w.start()
	// Two unchanged steps push the backoff to 80ms; wait until the loop is
	// inside that longer sleep.
	deadline := time.Now().Add(time.Second)
	for steps.Load() < 3 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	before := steps.Load()
	closed := time.Now()
	close(polled)
	for steps.Load() == before && time.Since(closed) < 200*time.Millisecond {
		time.Sleep(time.Millisecond)
	}
	if steps.Load() == before || time.Since(closed) > 40*time.Millisecond {
		t.Fatal("wake did not step at once", before, steps.Load(), time.Since(closed))
	}
	mu.Lock()
	defer mu.Unlock()
	if reasons[0].Cause != StepFull || reasons[1].Cause != StepTimer {
		t.Fatal(reasons)
	}
	woken := reasons[before]
	if woken.Cause != StepWake || len(woken.Events) != 1 || woken.Events[0].Action != "wall" || len(woken.Families) != 1 || woken.Families[0] != bridge.FactRooms || woken.Authority {
		t.Fatal(woken)
	}
	if outcomes, _ := w.config.Wake.Take(); outcomes["wall"].Attempt != 1 {
		t.Fatal("shared signal missed the wake", outcomes)
	}
	if drained := w.wake.TakeInvalidated(); len(drained.Events) != 0 || len(drained.Families) != 0 {
		t.Fatal("step wake not drained", drained)
	}
}

// A native build that ignores wait_ms returns at once: the loop keeps the
// PollInterval cadence instead of spinning; one that waited re-polls at once.
// Both are read from the time the loop takes to reach a poll count, which a
// loaded machine can only lengthen: the cadence case takes at least its
// intervals, and the waiting case, whose interval is far longer than the
// polls it must fit, stays under a single one. (#374: a fixed sleep and a
// count window failed once in four -race runs.)
func TestClockWorkerLongPollCadence(t *testing.T) {
	t.Parallel()
	const target = 7
	for _, waits := range []bool{false, true} {
		w := clockLoopFixture(t)
		w.config.PollInterval = 30 * time.Millisecond
		if waits {
			w.config.PollInterval = 500 * time.Millisecond
		}
		w.config.PollWait = 10 * time.Millisecond
		var polls atomic.Int32
		reached := make(chan struct{})
		w.poll = func(ctx context.Context, _ time.Duration) (ClockPollResult, error) {
			if waits {
				time.Sleep(w.config.PollWait)
			}
			if polls.Add(1) == target {
				close(reached)
			}
			return ClockPollResult{}, nil
		}
		started := time.Now()
		w.start()
		select {
		case <-reached:
		case <-time.After(5 * time.Second):
			t.Fatal("poll loop stalled", waits, polls.Load())
		}
		elapsed := time.Since(started)
		if !waits && elapsed < (target-1)*w.config.PollInterval {
			t.Fatal("early return spun instead of keeping the cadence", elapsed)
		}
		if waits && elapsed >= w.config.PollInterval {
			t.Fatal("waited poll fell back to the cadence", elapsed)
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
