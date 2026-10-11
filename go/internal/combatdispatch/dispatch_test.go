package combatdispatch

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestDispatchReducesInSeedOrderNotCompletionOrder(t *testing.T) {
	var mu sync.Mutex
	var done []int
	report, err := Dispatch(context.Background(), []int{5, 3, 6, 1, 4, 2}, 2, "test", func(_ context.Context, _, seed int) (int, error) {
		// Later seeds finish sooner.
		time.Sleep(time.Duration(7-seed) * 5 * time.Millisecond)
		mu.Lock()
		done = append(done, seed)
		mu.Unlock()
		return seed * 10, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if done[0] == 1 && done[1] == 2 {
		t.Fatalf("runs completed in seed order %v; the test needs them out of order", done)
	}
	for i, run := range report.Runs {
		if run.Seed != i+1 || run.Value != (i+1)*10 {
			t.Fatalf("run %d = %+v, want seed %d", i, run, i+1)
		}
	}
	if report.Workers != 2 || report.Load != "test" {
		t.Fatalf("report = %+v", report)
	}
}

func TestDispatchNeverRunsMoreThanTwoAtOnce(t *testing.T) {
	var active, peak atomic.Int32
	_, err := Dispatch(context.Background(), []int{1, 2, 3, 4, 5, 6}, 2, "", func(context.Context, int, int) (int, error) {
		n := active.Add(1)
		for p := peak.Load(); n > p && !peak.CompareAndSwap(p, n); p = peak.Load() {
		}
		time.Sleep(5 * time.Millisecond)
		active.Add(-1)
		return 0, nil
	})
	if err != nil || peak.Load() != 2 {
		t.Fatalf("peak %d err %v, want peak 2", peak.Load(), err)
	}
}

func TestDispatchRefusesBadWorkersAndDuplicateSeeds(t *testing.T) {
	ran := false
	fn := func(context.Context, int, int) (int, error) { ran = true; return 0, nil }
	for _, workers := range []int{0, 3, 4, -1} {
		if _, err := Dispatch(context.Background(), []int{1, 2}, workers, "", fn); err == nil {
			t.Fatalf("%d workers accepted", workers)
		}
	}
	if _, err := Dispatch(context.Background(), []int{1, 1}, 2, "", fn); err == nil {
		t.Fatal("duplicate seeds accepted")
	}
	if ran {
		t.Fatal("a refused dispatch ran a seed")
	}
}

func TestDispatchRecordsFailureAndKeepsTheOthers(t *testing.T) {
	report, err := Dispatch(context.Background(), []int{2, 1, 3}, 2, "", func(_ context.Context, _, seed int) (int, error) {
		if seed == 2 {
			return 0, errors.New("boom")
		}
		return seed, nil
	})
	if err == nil || report.Runs[1].Error != "boom" || report.Runs[2].Value != 3 {
		t.Fatalf("err %v report %+v", err, report)
	}
}
