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
