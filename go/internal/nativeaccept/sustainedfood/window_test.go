package sustainedfood

import "testing"

func TestTickWindowReachedAfterConfiguredTicks(t *testing.T) {
	w := newTickWindow(2500)
	if w.reached() {
		t.Fatal("unsampled window must not be reached")
	}
	w.observe(120000)
	w.observe(121000)
	if w.reached() {
		t.Fatalf("1000 ticks elapsed must not reach a 2500 window: %+v", w)
	}
	w.observe(122500)
	if !w.reached() || w.Elapsed != 2500 || w.FirstTick != 120000 || w.LastTick != 122500 {
		t.Fatalf("window not reached at 2500 elapsed: %+v", w)
	}
}

func TestTickWindowIgnoresRewindsAndZeroLength(t *testing.T) {
	w := newTickWindow(0)
	w.observe(500)
	w.observe(90000)
	if w.reached() {
		t.Fatal("a zero window watches on wall-clock alone and is never reached")
	}
	if w.Elapsed != 89500 {
		t.Fatalf("elapsed ticks still tracked with a zero window: %+v", w)
	}
	w.observe(100)
	if w.LastTick != 90000 || w.Elapsed != 89500 {
		t.Fatalf("a tick below the anchor must be ignored: %+v", w)
	}
}

func TestLiveTickReadsGameTick(t *testing.T) {
	call := func(body map[string]any, status int) func(string, string, map[string]any, string) (map[string]any, int, error) {
		return func(string, string, map[string]any, string) (map[string]any, int, error) { return body, status, nil }
	}
	if tick, ok := liveTick(call(map[string]any{"game": map[string]any{"tick": float64(4200)}}, 200)); !ok || tick != 4200 {
		t.Fatalf("tick=%d ok=%v", tick, ok)
	}
	if _, ok := liveTick(call(map[string]any{"game": map[string]any{"tick": nil}}, 200)); ok {
		t.Fatal("a stale snapshot without a tick must not count")
	}
	if _, ok := liveTick(call(map[string]any{}, 503)); ok {
		t.Fatal("a failed read must not count")
	}
}
