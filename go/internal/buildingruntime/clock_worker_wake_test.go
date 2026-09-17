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
	w.poll = func(context.Context) (ClockPollResult, error) {
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
