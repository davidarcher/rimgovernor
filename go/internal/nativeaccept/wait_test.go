package nativeaccept

import (
	"context"
	"errors"
	"testing"
	"time"
)

func TestWaitProgressDone(t *testing.T) {
	n := 0
	err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond, Stall: time.Second, Ceiling: time.Second}, func(context.Context) (string, bool, error) {
		n++
		return Signature("round", n), n >= 3, nil
	})
	if err != nil || n != 3 {
		t.Fatalf("err=%v rounds=%d", err, n)
	}
}

func TestWaitProgressStallsBeforeCeiling(t *testing.T) {
	start := time.Now()
	err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond, Stall: 30 * time.Millisecond, Ceiling: 10 * time.Second}, func(context.Context) (string, bool, error) {
		return "same", false, nil
	})
	var w *WaitError
	if !errors.As(err, &w) || w.Outcome != WaitStalled || !IsStalled(err) {
		t.Fatalf("want stalled, got %v", err)
	}
	if w.Signature != "same" || w.Rounds < 2 || w.Quiet < 30*time.Millisecond {
		t.Fatalf("bad detail: %+v", w)
	}
	if time.Since(start) > 2*time.Second {
		t.Fatalf("stall took %s", time.Since(start))
	}
}

func TestWaitProgressChangingSignatureHitsCeiling(t *testing.T) {
	n := 0
	err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond, Stall: 500 * time.Millisecond, Ceiling: 40 * time.Millisecond}, func(context.Context) (string, bool, error) {
		n++
		return Signature(n), false, nil
	})
	var w *WaitError
	if !errors.As(err, &w) || w.Outcome != WaitCeiling || IsStalled(err) {
		t.Fatalf("want ceiling, got %v", err)
	}
	if w.Quiet > 20*time.Millisecond {
		t.Fatalf("signature was changing; quiet=%s", w.Quiet)
	}
}

func TestWaitProgressTerminal(t *testing.T) {
	cause := errors.New("serve exited")
	calls := 0
	err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond, Terminal: func() error {
		calls++
		if calls == 2 {
			return cause
		}
		return nil
	}}, func(context.Context) (string, bool, error) { return "x", false, nil })
	var w *WaitError
	if !errors.As(err, &w) || w.Outcome != WaitTerminal || !errors.Is(err, cause) {
		t.Fatalf("want terminal wrapping cause, got %v", err)
	}
}

func TestWaitProgressProbeErrorAndContext(t *testing.T) {
	probeErr := errors.New("store")
	if err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond}, func(context.Context) (string, bool, error) { return "", false, probeErr }); !errors.Is(err, probeErr) {
		t.Fatalf("got %v", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := WaitProgress(ctx, Wait{Interval: time.Minute}, func(context.Context) (string, bool, error) { return "", false, nil }); !errors.Is(err, context.Canceled) {
		t.Fatalf("got %v", err)
	}
}

func TestWaitProgressTickBudget(t *testing.T) {
	tick := uint64(1000)
	n := 0
	err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond, Stall: 10 * time.Second, Ticks: 250, Tick: func(context.Context) (uint64, error) {
		tick += 100
		return tick, nil
	}}, func(context.Context) (string, bool, error) {
		n++
		return Signature("round", n), false, nil
	})
	var w *WaitError
	if !errors.As(err, &w) || w.Outcome != WaitTicks {
		t.Fatalf("want ticks outcome, got %v", err)
	}
	// Budget 250 past the first probe's tick (1100): 1200, 1300 are within, 1400 is over.
	if w.TicksElapsed != 300 || n != 4 {
		t.Fatalf("bad detail: elapsed=%d rounds=%d %+v", w.TicksElapsed, n, w)
	}
}

func TestWaitProgressTickBudgetDoneInTime(t *testing.T) {
	tick := uint64(0)
	err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond, Ticks: 500, Tick: func(context.Context) (uint64, error) {
		tick += 100
		return tick, nil
	}}, func(context.Context) (string, bool, error) {
		return "", tick >= 300, nil
	})
	if err != nil {
		t.Fatalf("unexpected: %v", err)
	}
}

func TestWaitStatsRecordTheLongestQuietSpan(t *testing.T) {
	before := WaitStats()
	waits, stalled := before["waits"].(int), before["stalled"].(int)
	// A wait whose signature moves on every probe and finishes.
	n := 0
	if err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond, Stall: time.Second}, func(context.Context) (string, bool, error) {
		n++
		return Signature(n), n >= 3, nil
	}); err != nil {
		t.Fatal(err)
	}
	// A wait that stalls on "held" after 30ms.
	err := WaitProgress(context.Background(), Wait{Interval: time.Millisecond, Stall: 30 * time.Millisecond}, func(context.Context) (string, bool, error) {
		return "held", false, nil
	})
	if !IsStalled(err) {
		t.Fatalf("want stalled, got %v", err)
	}
	after := WaitStats()
	if after["waits"].(int) != waits+2 || after["stalled"].(int) != stalled+1 {
		t.Fatalf("counts %v -> %v", before, after)
	}
	if after["max_quiet_ms"].(int64) < 30 {
		t.Fatalf("max_quiet_ms %v", after["max_quiet_ms"])
	}
	if after["stall_budget_ms"].(int64) != StallBudget().Milliseconds() {
		t.Fatalf("stall_budget_ms %v", after["stall_budget_ms"])
	}
}

func TestDefaultStallIsMinutesNotTens(t *testing.T) {
	t.Setenv(StallEnv, "")
	if StallBudget() != DefaultStall || DefaultStall > 5*time.Minute {
		t.Fatalf("StallBudget = %s (DefaultStall %s)", StallBudget(), DefaultStall)
	}
	t.Setenv(StallEnv, "90s")
	if StallBudget() != 90*time.Second {
		t.Fatalf("override ignored: %s", StallBudget())
	}
}
