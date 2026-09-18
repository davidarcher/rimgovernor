package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
// assignment, acquisition, ...) still to be made is pause-bound: once
// dispatched its outcome is observed on a running map like any other.
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
		{domain.AcquisitionAction, pending, true},
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
