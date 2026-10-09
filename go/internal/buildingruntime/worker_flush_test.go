package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

// A step's N dispatches all run deferred and the step flushes once, on a
// normal end, on a budget yield and on a dispatch error; a step
// that dispatches nothing does not flush.
func TestWorkerDefersDispatchesAndFlushesOncePerStep(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	workerPending(t, w, "one", true)
	workerPending(t, w, "two", true)
	workerPending(t, w, "three", true)
	var flushes, deferred, plain int
	var fail error
	w.config.Flush = func(context.Context) error { flushes++; return nil }
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		if bridge.DeferredSnapshot(ctx) {
			deferred++
		} else {
			plain++
		}
		plan, err := db.LoadPlan(ctx, p)
		if err == nil {
			err = fail
		}
		return executor.Result{Progress: plan.Progress[0]}, err
	}
	step := func(budget int) {
		t.Helper()
		w.waits = map[domain.ActionID]workerWait{}
		w.config.MaxDispatches = budget
		flushes, deferred, plain = 0, 0, 0
		_ = w.step(context.Background(), time.Now())
	}
	step(0)
	if deferred != 3 || plain != 0 || flushes != 1 {
		t.Fatalf("normal end: deferred %d plain %d flushes %d", deferred, plain, flushes)
	}
	step(2)
	if deferred != 2 || flushes != 1 {
		t.Fatalf("budget yield: deferred %d flushes %d", deferred, flushes)
	}
	fail = context.Canceled
	w.config.StepTimeout = time.Second
	step(0)
	if deferred == 0 || flushes != 1 {
		t.Fatalf("error: deferred %d flushes %d", deferred, flushes)
	}
	fail = nil
	w.waits = map[domain.ActionID]workerWait{}
	flushes = 0
	// One instant for both steps: the 10 ms backoff must not lapse between
	// them on a loaded box.
	now := time.Now()
	if err := w.step(context.Background(), now); err != nil && !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	flushes = 0
	_ = w.step(context.Background(), now) // every action backed off
	if flushes != 0 {
		t.Fatalf("idle step flushed %d", flushes)
	}
	w.config.Flush = nil
	step(0)
	if plain != 3 || deferred != 0 {
		t.Fatalf("without a flush: deferred %d plain %d", deferred, plain)
	}
}
