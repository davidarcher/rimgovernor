package buildingruntime

import (
	"context"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// paceNative is a native clock under player acceleration: the game tick
// advances on the wall clock at the window's ceiling (the boosted rate
// without one), and a speed change takes at once, as native's ceiling
// takes at the next frame.
type paceNative struct {
	mu       sync.Mutex
	at       time.Time
	tick     float64
	ceiling  uint32
	requests []paceRequest
}

type paceRequest struct {
	tick    int64
	ceiling uint32
}

func (n *paceNative) rate() uint32 {
	if n.ceiling == 0 || n.ceiling >= PaceFullTicksPerSecond {
		return PaceFullTicksPerSecond
	}
	return n.ceiling
}

// advance integrates the tick to now. Callers hold mu.
func (n *paceNative) advance() {
	now := time.Now()
	n.tick += float64(n.rate()) * now.Sub(n.at).Seconds()
	n.at = now
}

func (n *paceNative) read() (int64, time.Time) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return int64(n.tick), n.at
}

func (n *paceNative) change(_ context.Context, ceiling uint32) error {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	n.requests = append(n.requests, paceRequest{int64(n.tick), ceiling})
	n.ceiling = ceiling
	if ceiling >= PaceReleaseTicksPerSecond {
		n.ceiling = 0
	}
	return nil
}

func (n *paceNative) log() []paceRequest {
	n.mu.Lock()
	defer n.mu.Unlock()
	return append([]paceRequest(nil), n.requests...)
}

// A critical wave that hangs under a full-speed window: the backoff lowers
// the ceiling before the evidence the wave read ages past the horizon, and
// no full-speed request is issued until a wave completes again; fresh
// waves then climb back, doubling, to a release.
func TestPaceBackoffLowersBeforeHorizonAndNeverReleasesWhileStale(t *testing.T) {
	const horizon domain.Tick = 3000
	native := &paceNative{at: time.Now()}
	backoff := newPaceBackoff(horizon, time.Now, native.change)
	ctx := context.Background()

	evidenceTick, readAt := native.read()
	done := backoff.Watch(ctx, readAt)
	// The wave hangs well past the horizon at full speed (333 ms).
	deadline := time.Now().Add(10 * time.Second)
	for len(native.log()) == 0 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	requests := native.log()
	if len(requests) != 1 || requests[0].ceiling != PaceFloorTicksPerSecond {
		t.Fatalf("stale wave requests %+v, want one floor ceiling", requests)
	}
	if age := requests[0].tick - evidenceTick; age >= int64(horizon) {
		t.Fatalf("backoff landed %d ticks after the evidence, past the %d-tick horizon", age, horizon)
	}
	// Still stale: a release is refused outright.
	backoff.set(ctx, 0, "test")
	if got := native.log(); len(got) != 1 {
		t.Fatalf("release issued while stale: %+v", got)
	}
	time.Sleep(50 * time.Millisecond)
	done(time.Since(readAt), true)
	if backoff.stale {
		t.Fatal("a completed wave left the evidence stale")
	}

	// Fast waves (evidence 5 ms old) earn full speed back one doubling at
	// a time, then a release.
	for range 12 {
		_, readAt := native.read()
		finish := backoff.Watch(ctx, readAt)
		finish(5*time.Millisecond, true)
	}
	requests = native.log()
	last := requests[len(requests)-1]
	if last.ceiling != PaceReleaseTicksPerSecond || backoff.Ceiling() != 0 {
		t.Fatalf("fresh waves did not release: %+v", requests)
	}
	for i := 2; i < len(requests)-1; i++ {
		if requests[i].ceiling > 2*requests[i-1].ceiling {
			t.Fatalf("ceiling jumped more than doubling: %+v", requests)
		}
	}
	if backoff.released != 1 {
		t.Fatalf("released %d times, want 1", backoff.released)
	}
}

// A wave slower than the margin allows at full speed lowers the ceiling to
// the rate it fits, predictively, before any evidence goes stale; the next
// window starts under it.
func TestPaceBackoffLowersToTheRateAWaveFits(t *testing.T) {
	native := &paceNative{at: time.Now()}
	backoff := newPaceBackoff(300, time.Now, native.change)
	backoff.fresh(context.Background(), time.Second)
	// 300 ticks x 0.5 margin over a one-second wave.
	if got := backoff.Ceiling(); got != 150 {
		t.Fatalf("ceiling %d, want 150", got)
	}
	backoff.fresh(context.Background(), time.Second)
	if requests := native.log(); len(requests) != 1 {
		t.Fatalf("an unchanged target reissued the ceiling: %+v", requests)
	}
}
