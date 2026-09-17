package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

// A wake naming an attempt reconciles that action first and ignores its
// backoff; the rotation resumes fairly afterwards.
func TestWorkerWakeReconcilesNamedAttemptFirst(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	w.config.Wake = NewWakeSignal()
	first := workerPending(t, w, "one", true)
	second := workerPending(t, w, "two", true)
	var order []domain.ActionID
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		order = append(order, a)
		plan, err := db.LoadPlan(ctx, p)
		return executor.Result{Progress: plan.Progress[0]}, err
	}
	now := time.Now()
	// Both actions run once and then wait out their backoff.
	for i := 0; i < 2; i++ {
		if err := w.step(context.Background(), now); err != nil {
			t.Fatal(err)
		}
	}
	if len(order) != 2 || order[0] == order[1] || (order[0] != first.Action && order[0] != second.Action) {
		t.Fatal(order)
	}
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 2 {
		t.Fatal("backoff ignored", order)
	}
	// The rotation would pick the other action next; the wake names the one
	// that just ran and its backoff no longer applies.
	last := order[1]
	w.config.Wake.Notify([]WakeOutcome{{Action: last, Attempt: 1, Terminal: true}}, false)
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 3 || order[2] != last || len(w.focus) != 0 {
		t.Fatal(order, w.focus)
	}
	// A wake for an action that no plan carries is dropped.
	w.config.Wake.Notify([]WakeOutcome{{Action: "ghost", Attempt: 1}}, false)
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 3 || len(w.focus) != 0 {
		t.Fatal(order, w.focus)
	}
}
