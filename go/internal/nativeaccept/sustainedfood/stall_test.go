package sustainedfood

import (
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func TestTickStall(t *testing.T) {
	t0 := time.Unix(0, 0)
	limit := 10 * time.Minute

	frozen := newTickStall(limit, t0)
	frozen.observe(85165, t0)
	if _, stalled := frozen.check(t0.Add(limit)); stalled {
		t.Fatal("stalled at exactly the limit")
	}
	if err, stalled := frozen.check(t0.Add(limit + time.Second)); !stalled || err == nil {
		t.Fatal("frozen tick not reported")
	}

	advancing := newTickStall(limit, t0)
	for i := 0; i < 30; i++ {
		now := t0.Add(time.Duration(i) * time.Minute)
		advancing.observe(uint64(i*600), now)
		if _, stalled := advancing.check(now); stalled {
			t.Fatalf("advancing tick reported stalled at minute %d", i)
		}
	}

	unread := newTickStall(limit, t0)
	if _, stalled := unread.check(t0.Add(limit + time.Second)); !stalled {
		t.Fatal("unreadable tick not reported")
	}

	paused := newTickStall(limit, t0)
	paused.observe(1, t0)
	paused.reset(t0.Add(8 * time.Minute))
	if _, stalled := paused.check(t0.Add(15 * time.Minute)); stalled {
		t.Fatal("checkpoint pause counted against the stall limit")
	}
}

func TestTickStallIdle(t *testing.T) {
	t0 := time.Unix(0, 0)
	grace := 30 * time.Second
	s := newTickStall(10*time.Minute, t0)
	s.observe(123460, t0)
	if s.idle(t0.Add(time.Minute), grace) {
		t.Fatal("frozen tick without a no_work refusal reported idle")
	}
	s.refusedNoWork()
	if s.idle(t0.Add(grace-time.Second), grace) {
		t.Fatal("idle before the grace elapsed")
	}
	if !s.idle(t0.Add(grace), grace) {
		t.Fatal("parked game not reported idle")
	}
	s.observe(123461, t0.Add(grace))
	if s.idle(t0.Add(2*grace), grace) {
		t.Fatal("an advancing tick did not clear the refusal")
	}
}

func TestNoWorkRefusal(t *testing.T) {
	row := na.FlightRow{Kind: "admission_refused", Payload: map[string]any{"refused": []any{"no_work"}}}
	if !NoWorkRefusal(row) {
		t.Fatal("no_work refusal not recognised")
	}
	row.Payload = map[string]any{"refused": []any{"critical_wave_budget"}}
	if NoWorkRefusal(row) {
		t.Fatal("budget refusal read as no_work")
	}
}
