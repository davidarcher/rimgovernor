package buildingruntime

import (
	"context"
	"sync/atomic"
	"testing"
	"testing/synctest"
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
	synctest.Test(t, func(t *testing.T) {
		w := clockLoopFixture(t)
		w.config.StepInterval = 20 * time.Millisecond
		w.config.MaxBackoff = 2 * time.Second
		w.config.PollTimeout = time.Second
		w.config.Wake = NewWakeSignal()
		var reasons []StepReason
		w.step = func(_ context.Context, reason StepReason) (ClockSchedulerResult, error) {
			reasons = append(reasons, reason)
			return ClockSchedulerResult{}, nil
		}
		polled := make(chan struct{})
		defer close(polled)
		var polls atomic.Int32
		w.poll = func(context.Context, time.Duration) (ClockPollResult, error) {
			if polls.Add(1) != 2 {
				return ClockPollResult{}, nil
			}
			<-polled
			return ClockPollResult{Captured: true, Wake: []WakeOutcome{{Action: "wall", Attempt: 1, Terminal: true}}, Invalidated: []bridge.FactFamily{bridge.FactRooms}}, nil
		}
		w.start()
		// Run the full step and two timer steps, then park every loop. The
		// unchanged results leave the step loop in its 80ms backoff.
		synctest.Wait()
		time.Sleep(20 * time.Millisecond)
		synctest.Wait()
		time.Sleep(40 * time.Millisecond)
		synctest.Wait()
		if len(reasons) != 3 {
			t.Fatal("expected full step and two timer steps", reasons)
		}
		before := len(reasons)
		now := time.Now()
		polled <- struct{}{}
		synctest.Wait()
		// No virtual time passed: only the captured event can end this sleep.
		if len(reasons) != before+1 || !time.Now().Equal(now) {
			t.Fatal("capture did not wake the backed-off step", reasons, time.Since(now))
		}
		if reasons[0].Cause != StepFull || reasons[1].Cause != StepTimer || reasons[2].Cause != StepTimer {
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
	})
}

// A native build that ignores wait_ms returns at once: the loop keeps the
// PollInterval cadence instead of spinning; one that waited re-polls at once.
// Virtual time measures the exact cadence independently of host scheduling:
// ignored waits add PollInterval, while held reads add only PollWait.
func TestClockWorkerLongPollCadence(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
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
			want := (target - 1) * w.config.PollInterval
			if waits {
				want = target * w.config.PollWait
			}
			if elapsed != want {
				t.Fatalf("held %v: poll cadence = %v, want %v", waits, elapsed, want)
			}
		}
	})
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
