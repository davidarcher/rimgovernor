package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

// A wake naming an attempt reconciles that action first and ignores its
// backoff; every other action's backoff is dropped too, since the latched
// outcome may have unblocked it (issue #162), and the rotation resumes
// fairly afterwards.
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
	// The wake dropped the other action's backoff: it runs next.
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 4 || order[3] == last {
		t.Fatal(order)
	}
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 4 {
		t.Fatal("backoff ignored", order)
	}
	// A wake for an action that no plan carries is dropped from the focus.
	w.config.Wake.Notify([]WakeOutcome{{Action: "ghost", Attempt: 1}}, false)
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(w.focus) != 0 {
		t.Fatal(order, w.focus)
	}
}

// A wake naming two attempts reconciles both in consecutive steps: the loop
// steps again at once while woken actions remain instead of idling out a
// StepInterval between them (#108).
func TestWorkerWakeStepsConsecutivelyWhileFocused(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	w.config.Wake = NewWakeSignal()
	// The ticker never fires during the test: every step past the first is
	// a wake or a focus re-step.
	w.config.StepInterval = time.Hour
	w.ctx, w.cancel = context.WithCancel(context.Background())
	defer w.cancel()
	first := workerPending(t, w, "one", true)
	second := workerPending(t, w, "two", true)
	ran := make(chan domain.ActionID, 8)
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		ran <- a
		plan, err := db.LoadPlan(ctx, p)
		return executor.Result{Progress: plan.Progress[0]}, err
	}
	go w.steps()
	next := func() domain.ActionID {
		select {
		case a := <-ran:
			return a
		case <-time.After(5 * time.Second):
			t.Fatal("no step within the deadline")
			return ""
		}
	}
	// The first step runs one action; the loop then waits on the hour ticker.
	initial := next()
	select {
	case a := <-ran:
		t.Fatal("unexpected second step without a wake", a)
	case <-time.After(50 * time.Millisecond):
	}
	// One wake names both attempts: the first focused step runs one, and the
	// remaining focus triggers the next step at once.
	w.config.Wake.Notify([]WakeOutcome{{Action: first.Action, Attempt: 1, Terminal: true}, {Action: second.Action, Attempt: 1, Terminal: true}}, false)
	woken := []domain.ActionID{next(), next()}
	if woken[0] == woken[1] || (woken[0] != first.Action && woken[0] != second.Action) || (woken[1] != first.Action && woken[1] != second.Action) {
		t.Fatal(initial, woken)
	}
	// Both are reconciled; the loop is idle again until the next wake.
	select {
	case a := <-ran:
		t.Fatal("step after the focus drained", a)
	case <-time.After(50 * time.Millisecond):
	}
}

// A step that advanced an action steps again at once: the successor the
// advance unblocked dispatches before the clock readmits a window instead
// of a StepInterval later (issue #162). A step that changed nothing idles.
func TestWorkerAdvanceStepsAgainAtOnce(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	w.config.Wake = NewWakeSignal()
	w.config.StepInterval = time.Hour
	w.ctx, w.cancel = context.WithCancel(context.Background())
	defer w.cancel()
	first := workerPending(t, w, "one", true)
	second := workerPending(t, w, "two", true)
	ran := make(chan domain.ActionID, 8)
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		ran <- a
		plan, err := db.LoadPlan(ctx, p)
		if err != nil {
			return executor.Result{}, err
		}
		// The run settles the dispatched action: a stage change.
		if v := plan.Progress[0].View(); v.Stage == domain.Dispatched {
			progress, err := db.RecordReceipt(ctx, p, a, v.Attempt, domain.ReceiptRefused)
			return executor.Result{Progress: progress}, err
		}
		return executor.Result{Progress: plan.Progress[0]}, nil
	}
	go w.steps()
	next := func() domain.ActionID {
		select {
		case a := <-ran:
			return a
		case <-time.After(5 * time.Second):
			t.Fatal("no step within the deadline")
			return ""
		}
	}
	// The first step advances one action; the loop steps again and
	// advances the other without waiting on the hour ticker.
	got := []domain.ActionID{next(), next()}
	if got[0] == got[1] || (got[0] != first.Action && got[0] != second.Action) || (got[1] != first.Action && got[1] != second.Action) {
		t.Fatal(got)
	}
	// Both actions are settled; the loop is idle until the next wake or tick.
	select {
	case a := <-ran:
		t.Fatal("step after nothing advanced", a)
	case <-time.After(100 * time.Millisecond):
	}
}
