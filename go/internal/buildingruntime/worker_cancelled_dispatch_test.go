package buildingruntime

import (
	"context"
	"fmt"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

// A dispatch whose own context is cancelled while the step's is live
// retries once under the step's; a cancellation that survives the retry
// step after step settles the undispatched action cancelled rather than
// parking it at attempt 0 for ever (#671).
func TestWorkerCancelledDispatchRetriesThenSettles(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	v := workerPending(t, w, "cancelled", false)
	f.mu.Lock()
	f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	f.mu.Unlock()
	cancelled := fmt.Errorf("bridge transport failure: games_call_tool: %w", context.Canceled)
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		plan, err := db.LoadPlan(ctx, p)
		if err != nil {
			return executor.Result{}, err
		}
		return executor.Result{Progress: plan.Progress[0], NativeCalled: true}, cancelled
	}
	now := time.Now()
	for step := 1; step <= workerCancelledSettle; step++ {
		_ = w.step(context.Background(), now)
		if got := f.runs.Load(); got != int32(2*step) {
			t.Fatalf("step %d: %d runs, want a retry per step", step, got)
		}
		now = now.Add(time.Hour)
	}
	plan, err := db.LoadPlan(context.Background(), v.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if got := plan.Progress[0].View().Stage; got != domain.Cancelled {
		t.Fatalf("stage %s after %d cancelled steps, want cancelled", got, workerCancelledSettle)
	}
	_ = w.step(context.Background(), now)
	if got := f.runs.Load(); got != int32(2*workerCancelledSettle) {
		t.Fatalf("settled action dispatched again: %d runs", got)
	}
}

// A retry that succeeds leaves nothing to settle.
func TestWorkerCancelledDispatchRetrySucceeds(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	v := workerPending(t, w, "retried", false)
	f.mu.Lock()
	f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	f.mu.Unlock()
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		plan, err := db.LoadPlan(ctx, p)
		if err != nil {
			return executor.Result{}, err
		}
		if f.runs.Load()%2 == 1 {
			err = context.Canceled
		}
		return executor.Result{Progress: plan.Progress[0], NativeCalled: true}, err
	}
	now := time.Now()
	for step := 0; step < workerCancelledSettle+1; step++ {
		if err := w.step(context.Background(), now); err != nil {
			t.Fatal(err)
		}
		now = now.Add(time.Hour)
	}
	plan, err := db.LoadPlan(context.Background(), v.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if got := plan.Progress[0].View().Stage; got == domain.Cancelled {
		t.Fatal("a dispatch its retry recovered was settled")
	}
}
