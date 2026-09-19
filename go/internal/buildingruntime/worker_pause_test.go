package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// A committed Stopped event reaches the Worker as a stop, once, beside the
// ordinary wake evidence; a page without one leaves the flag clear (#129).
func TestWakeSignalCarriesStop(t *testing.T) {
	t.Parallel()
	page := &k.EventsPage{Events: []*k.Event{
		{Cursor: proto.Int64(1), Event: &k.Event_OperationOutcome{OperationOutcome: clockPollOutcome(1)}},
		{Cursor: proto.Int64(2), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum()}}},
	}}
	outcomes, _, _, stopped, _ := clockPageWakeStopped(page)
	if !stopped || len(outcomes) != 1 {
		t.Fatal(stopped, outcomes)
	}
	if _, _, _, stopped, _ := clockPageWakeStopped(&k.EventsPage{Events: page.Events[:1]}); stopped {
		t.Fatal("no Stopped event on the page")
	}
	w := NewWakeSignal()
	w.NotifyStopped(outcomes, nil, false, true)
	if woken, _ := w.Take(); len(woken) != 1 {
		t.Fatal(woken)
	}
	if !w.TakeStopped() || w.TakeStopped() {
		t.Fatal("stop is drained once")
	}
	w.NotifyInvalidated(nil, nil, true)
	if w.TakeStopped() {
		t.Fatal("an invalidation is not a stop")
	}
	var none *WakeSignal
	if none.TakeStopped() {
		t.Fatal("nil signal")
	}
}

// Only an admission whose native tool needs a paused map (excavation, bed
// assignment, ...) still to be made is pause-bound: once dispatched its
// outcome is observed on a running map like any other. The kinds whose
// operations validate at apply time dispatch live (#243).
func TestWorkerPauseBound(t *testing.T) {
	t.Parallel()
	pending := domain.ProgressView{Stage: domain.Pending}
	prepared := domain.ProgressView{Stage: domain.Prepared}
	dispatched := domain.ProgressView{Stage: domain.AwaitingObservation, Unresolved: true}
	completed := domain.ProgressView{Stage: domain.Completed}
	for _, tc := range []struct {
		kind domain.ActionKind
		view domain.ProgressView
		want bool
	}{
		{domain.ExcavationAction, pending, true},
		{domain.ExcavationAction, prepared, true},
		{domain.BedAssignAction, pending, true},
		{domain.AcquisitionAction, pending, false},
		{domain.MineAcquisitionAction, prepared, false},
		{domain.HusbandryAction, pending, false},
		{domain.WallRemovalAction, prepared, true},
		{domain.ProductionBillAction, pending, false},
		{domain.ZoneCreateAction, pending, false},
		{domain.ExcavationAction, dispatched, false},
		{domain.ExcavationAction, completed, false},
		{domain.BuildingAction, pending, false},
	} {
		if got := workerPauseBound(tc.kind, tc.view); got != tc.want {
			t.Errorf("%s %s: got %v want %v", tc.kind, tc.view.Stage, got, tc.want)
		}
	}
}

// A stop holds the next window only while an attached Worker still has
// pause-bound admissions to try for that stop: without a Worker, or once the
// Worker reports none, the signal is drained; a report for an earlier stop
// is stale and leaves the hold in place.
func TestWakeSignalPauseDrain(t *testing.T) {
	t.Parallel()
	drained := func(w *WakeSignal) bool {
		select {
		case <-w.PauseDrained():
			return true
		default:
			return false
		}
	}
	var none *WakeSignal
	if !drained(none) {
		t.Fatal("nil signal never holds")
	}
	w := NewWakeSignal()
	w.NotifyStopped(nil, nil, false, true)
	if !drained(w) {
		t.Fatal("no Worker attached: nothing to wait for")
	}
	detach := w.AttachWorker()
	stop, stopped := w.TakeStop()
	if !stopped || stop != 1 {
		t.Fatal(stop, stopped)
	}
	w.NotifyStopped(nil, nil, false, true)
	if drained(w) {
		t.Fatal("a stop with a Worker attached waits for its report")
	}
	w.ReportPauseWork(stop, 0)
	if drained(w) {
		t.Fatal("a report for the earlier stop is stale")
	}
	stop, _ = w.TakeStop()
	w.ReportPauseWork(stop, 2)
	if drained(w) {
		t.Fatal("two admissions still to try")
	}
	w.ReportPauseWork(stop, 0)
	if !drained(w) {
		t.Fatal("all tried")
	}
	w.NotifyStopped(nil, nil, false, true)
	if drained(w) {
		t.Fatal("the next stop holds again")
	}
	detach()
	if !drained(w) {
		t.Fatal("detaching the last Worker releases the hold")
	}
}

// Each report that lowers the outstanding count for the current stop
// signals progress, so the step loop's hold restarts its idle bound per
// admission instead of running out after the first (#211); a stale stop,
// an unchanged count and the final report to zero do not.
func TestWakeSignalPauseProgress(t *testing.T) {
	t.Parallel()
	progressed := func(ch <-chan struct{}) bool {
		select {
		case <-ch:
			return true
		default:
			return false
		}
	}
	var none *WakeSignal
	if none.PauseProgressed() != nil {
		t.Fatal("nil signal never progresses")
	}
	w := NewWakeSignal()
	defer w.AttachWorker()()
	w.NotifyStopped(nil, nil, false, true)
	stop, _ := w.TakeStop()
	ch := w.PauseProgressed()
	w.ReportPauseWork(stop, 3)
	if progressed(ch) {
		t.Fatal("the first report is the backlog, not progress")
	}
	w.ReportPauseWork(stop, 3)
	if progressed(ch) {
		t.Fatal("an unchanged count is not progress")
	}
	w.ReportPauseWork(stop+1, 2)
	if progressed(ch) {
		t.Fatal("a report for another stop is stale")
	}
	w.ReportPauseWork(stop, 2)
	if !progressed(ch) {
		t.Fatal("one admission tried")
	}
	ch = w.PauseProgressed()
	if progressed(ch) {
		t.Fatal("a fresh channel waits for the next admission")
	}
	w.ReportPauseWork(stop, 0)
	if progressed(ch) || !progressed(w.PauseDrained()) {
		t.Fatal("the last report drains rather than progresses")
	}
}

// A dispatch held on stale_facts between windows is retried at once and
// counted as pause work until then, so the stop holds the next window for
// the retry rather than letting the game's own work scanner take the
// order's target first (#288); one retry per stop, so a hold that survives
// it releases the clock, and a hold under a running window is left to its
// backoff.
func TestWorkerStaleHoldHoldsClockForOneRetry(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	v := workerPending(t, w, "stale", false)
	f.mu.Lock()
	f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	f.mu.Unlock()
	wake := NewWakeSignal()
	w.config.Wake = wake
	defer wake.AttachWorker()()
	running := false
	w.config.WindowRunning = func() bool { return running }
	held := true
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		plan, err := db.LoadPlan(ctx, p)
		if err != nil {
			t.Fatal(err)
		}
		result := executor.Result{Progress: plan.Progress[0]}
		if held {
			result.Refused = []policy.Refusal{{Action: a, Reason: policy.StaleFacts}}
			return result, executor.ErrHeld
		}
		return result, nil
	}
	wake.NotifyStopped(nil, nil, false, true)
	now := time.Now()
	step := func() {
		t.Helper()
		if err := w.step(context.Background(), now); err != nil && !errors.Is(err, executor.ErrHeld) {
			t.Fatal(err)
		}
		wake.ReportPauseWork(w.stopSeq, len(w.pauseFocus))
	}
	drained := func() bool {
		select {
		case <-wake.PauseDrained():
			return true
		default:
			return false
		}
	}
	step()
	if f.runs.Load() != 1 || drained() || len(w.focus) != 1 || !w.pauseFocus[v.Action] {
		t.Fatal("a stale hold at the stop is pause work with the retry focused", f.runs.Load(), drained(), w.focus, w.pauseFocus)
	}
	step()
	if f.runs.Load() != 2 || !drained() || len(w.pauseFocus) != 0 {
		t.Fatal("the retry ran at once and a surviving hold released the clock", f.runs.Load(), drained(), w.pauseFocus)
	}
	step()
	if f.runs.Load() != 2 {
		t.Fatal("a surviving hold is back on its backoff, not retried again this stop")
	}
	// The next stop is a new observation: the held dispatch is retried
	// there at once, off its backoff, and once only.
	wake.NotifyStopped(nil, nil, false, true)
	step()
	if f.runs.Load() != 3 || !drained() || len(w.pauseFocus) != 0 {
		t.Fatal("a new stop retries the held dispatch once", f.runs.Load(), drained(), w.pauseFocus)
	}
	step()
	if f.runs.Load() != 3 {
		t.Fatal("retried once per stop")
	}
	held = false
	wake.NotifyStopped(nil, nil, false, true)
	step()
	if f.runs.Load() != 4 || !drained() {
		t.Fatal("the retry dispatched and drained the stop", f.runs.Load(), drained())
	}
	// Under a running window the hold is ordinary backoff work.
	held, running = true, true
	wake.NotifyStopped(nil, nil, false, true)
	w.waits = map[domain.ActionID]workerWait{}
	step()
	if f.runs.Load() != 5 || !drained() || len(w.pauseFocus) != 0 {
		t.Fatal("a hold under a running window is not pause work", f.runs.Load(), drained(), w.pauseFocus)
	}
}

func TestWorkerHeldStale(t *testing.T) {
	t.Parallel()
	pending := domain.ProgressView{Stage: domain.Pending}
	stale := executor.Result{Refused: []policy.Refusal{{Reason: policy.StaleFacts}}}
	if !workerHeldStale(pending, stale, executor.ErrHeld) {
		t.Fatal("stale_facts refusal held")
	}
	if workerHeldStale(pending, stale, nil) || workerHeldStale(pending, stale, executor.ErrEvidence) {
		t.Fatal("only a hold counts")
	}
	if workerHeldStale(pending, executor.Result{Refused: []policy.Refusal{{Reason: policy.InsufficientStock}}}, executor.ErrHeld) {
		t.Fatal("a world-condition refusal is not stale facts")
	}
	if workerHeldStale(domain.ProgressView{Stage: domain.AwaitingObservation, Unresolved: true}, stale, executor.ErrHeld) {
		t.Fatal("a dispatched attempt is reconciliation, not a held dispatch")
	}
	if workerHeldStale(pending, executor.Result{}, executor.ErrHeld) {
		t.Fatal("a bare hold names no reason")
	}
}
