package sustainedfood

import (
	"testing"
	"time"
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
