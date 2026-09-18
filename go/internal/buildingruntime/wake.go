package buildingruntime

import (
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// WakeOutcome names one attempt whose terminal outcome the native clock
// latched, so the Worker reconciles that attempt next instead of rotating.
type WakeOutcome struct {
	Action   domain.ActionID
	Attempt  domain.AttemptID
	Terminal bool
}

// WakeSignal carries committed clock evidence from the poll loop to the
// scheduler step loop and the Worker. Only evidence the journal has stored
// wakes anyone; a wake is a hint to act now, never a fact by itself. Every
// method is nil-safe so timer-only callers and tests need no signal.
type WakeSignal struct {
	mu        sync.Mutex
	ch        chan struct{}
	pending   map[domain.ActionID]WakeOutcome
	families  map[bridge.FactFamily]bool
	authority bool
	// stopped records that a committed page carried a Stopped event: the
	// game is paused between windows, the only moment an admission that
	// needs a paused map (excavation, bed assignment) can be made.
	stopped bool
	// stopAt is the native stamp of the earliest stop still pending for
	// the step loop, zero when the page carried none; the step that acts
	// on it publishes the stop-to-step latency (issue #112).
	stopAt time.Time
	// stops counts committed stops; a Worker's pause-work report names the
	// stop it answers so a report from a step begun before the stop
	// cannot pass for the stop's own.
	stops uint64
	// stopsTaken is the stop count the step loop last drained.
	stopsTaken uint64
	// workers counts attached Workers: with none, no stop ever waits for
	// pause-bound work.
	workers int
	// pauseWork is how many pause-bound admissions the attached Worker
	// still has to try on the current stop, -1 until it has reported;
	// pauseIdle is closed while that count is zero.
	pauseWork int
	pauseIdle chan struct{}
	// pauseProgress is closed and replaced each time a report lowers the
	// count for the same stop: the Worker made an admission, so the stop
	// is worth holding a little longer (#211).
	pauseProgress chan struct{}
}

func NewWakeSignal() *WakeSignal {
	idle := make(chan struct{})
	close(idle)
	return &WakeSignal{ch: make(chan struct{}, 1), pending: map[domain.ActionID]WakeOutcome{}, families: map[bridge.FactFamily]bool{}, pauseIdle: idle, pauseProgress: make(chan struct{})}
}

// Notify merges outcomes into the pending set and signals without blocking.
func (w *WakeSignal) Notify(outcomes []WakeOutcome, authority bool) {
	w.NotifyInvalidated(outcomes, nil, authority)
}

// NotifyInvalidated is Notify carrying the fact families the committed
// page's ObservationInvalidated events named as well.
func (w *WakeSignal) NotifyInvalidated(outcomes []WakeOutcome, families []bridge.FactFamily, authority bool) {
	w.NotifyStopped(outcomes, families, authority, false)
}

// NotifyStopped is NotifyInvalidated that also records whether the page
// stopped the clock.
func (w *WakeSignal) NotifyStopped(outcomes []WakeOutcome, families []bridge.FactFamily, authority, stopped bool) {
	w.NotifyStopAt(outcomes, families, authority, stopped, time.Time{})
}

// NotifyStopAt is NotifyStopped carrying the native stamp of the stop
// (the Stopped event's observed_at), zero when unknown.
func (w *WakeSignal) NotifyStopAt(outcomes []WakeOutcome, families []bridge.FactFamily, authority, stopped bool, stopAt time.Time) {
	if w == nil {
		return
	}
	w.mu.Lock()
	for _, o := range outcomes {
		w.pending[o.Action] = o
	}
	for _, family := range families {
		w.families[family] = true
	}
	w.authority = w.authority || authority
	if stopped {
		w.stopped = true
		w.stops++
		if w.stopAt.IsZero() || !stopAt.IsZero() && stopAt.Before(w.stopAt) {
			w.stopAt = stopAt
		}
		if w.workers > 0 && w.pauseWork == 0 {
			w.pauseWork = -1
			w.pauseIdle = make(chan struct{})
		}
	}
	w.mu.Unlock()
	select {
	case w.ch <- struct{}{}:
	default:
	}
}

// C is closed-over by loops that also wait on a timer; a nil signal never fires.
func (w *WakeSignal) C() <-chan struct{} {
	if w == nil {
		return nil
	}
	return w.ch
}

// Take drains the pending outcomes and the authority flag; the invalidated
// families are left for TakeInvalidated.
func (w *WakeSignal) Take() (map[domain.ActionID]WakeOutcome, bool) {
	if w == nil {
		return nil, false
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	outcomes := w.pending
	authority := w.authority
	w.pending = map[domain.ActionID]WakeOutcome{}
	w.authority = false
	return outcomes, authority
}

// TakeStopped drains the stopped flag.
func (w *WakeSignal) TakeStopped() bool {
	_, stopped := w.TakeStop()
	return stopped
}

// TakeStop is TakeStopped that also names the latest committed stop, the
// one a pause-work report answers.
func (w *WakeSignal) TakeStop() (stop uint64, stopped bool) {
	if w == nil {
		return 0, false
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	stopped = w.stopped
	w.stopped = false
	return w.stops, stopped
}

// AttachWorker registers a Worker that will report its pause-bound work on
// every stop; the returned func detaches it and releases any wait.
func (w *WakeSignal) AttachWorker() func() {
	if w == nil {
		return func() {}
	}
	w.mu.Lock()
	w.workers++
	w.mu.Unlock()
	return func() {
		w.mu.Lock()
		defer w.mu.Unlock()
		w.workers--
		if w.workers == 0 {
			w.setPauseWorkLocked(0)
		}
	}
}

// ReportPauseWork records how many pause-bound admissions the Worker still
// has to try for the named stop. A report for an earlier stop is stale: the
// step it summarises began before the game paused.
func (w *WakeSignal) ReportPauseWork(stop uint64, remaining int) {
	if w == nil {
		return
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	if stop != w.stops {
		return
	}
	w.setPauseWorkLocked(remaining)
}

func (w *WakeSignal) setPauseWorkLocked(remaining int) {
	if remaining < 0 {
		remaining = 0
	}
	if remaining == 0 && w.pauseWork != 0 {
		close(w.pauseIdle)
	} else if remaining > 0 && w.pauseWork == 0 {
		w.pauseIdle = make(chan struct{})
	}
	if remaining > 0 && remaining < w.pauseWork {
		close(w.pauseProgress)
		w.pauseProgress = make(chan struct{})
	}
	w.pauseWork = remaining
}

// PauseProgressed is closed when the attached Worker's next report lowers
// its outstanding pause-bound work without draining it; a nil signal never
// progresses.
func (w *WakeSignal) PauseProgressed() <-chan struct{} {
	if w == nil {
		return nil
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	return w.pauseProgress
}

// PauseDrained is closed while no attached Worker has pause-bound work
// outstanding for the latest stop; a nil signal is always drained.
func (w *WakeSignal) PauseDrained() <-chan struct{} {
	if w == nil {
		return closedChan
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	return w.pauseIdle
}

var closedChan = func() chan struct{} {
	ch := make(chan struct{})
	close(ch)
	return ch
}()

// TakeInvalidated drains the pending outcomes, invalidated families, the
// authority flag and the pending stop into one step reason. The stopped
// flag itself is left for TakeStop: the Worker's pause-bound admissions
// answer it, not the step loop.
func (w *WakeSignal) TakeInvalidated() StepReason {
	reason := StepReason{Cause: StepWake}
	if w == nil {
		return reason
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	for _, o := range w.pending {
		reason.Events = append(reason.Events, o)
	}
	for family := range w.families {
		reason.Families = append(reason.Families, family)
	}
	reason.Authority = w.authority
	reason.Stopped, reason.StopAt = w.stops > w.stopsTaken, w.stopAt
	w.pending = map[domain.ActionID]WakeOutcome{}
	w.families = map[bridge.FactFamily]bool{}
	w.authority = false
	w.stopsTaken, w.stopAt = w.stops, time.Time{}
	return reason
}

// clockPageWakeStopped reads the wake-relevant facts out of one committed
// page: the latched outcomes, the families its ObservationInvalidated
// events named, whether authority changed, and whether the page carried a
// Stopped event with the earliest such event's native stamp (zero when the
// event carried none).
func clockPageWakeStopped(page *k.EventsPage) (outcomes []WakeOutcome, families []bridge.FactFamily, authority, stopped bool, stopAt time.Time) {
	seen := map[bridge.FactFamily]bool{}
	for _, event := range page.GetEvents() {
		switch v := event.Event.(type) {
		case *k.Event_OperationOutcome:
			outcomes = append(outcomes, wakeOutcome(v.OperationOutcome))
		case *k.Event_Stopped:
			stopped = true
			if at := event.GetObservedAtUnixMs(); at > 0 && (stopAt.IsZero() || time.UnixMilli(at).Before(stopAt)) {
				stopAt = time.UnixMilli(at)
			}
			if watch := v.Stopped.GetWatch(); watch != nil {
				outcomes = append(outcomes, wakeOutcome(watch.Outcome))
			}
		case *k.Event_AuthorityChanged:
			authority = true
		case *k.Event_ObservationInvalidated:
			for _, wire := range v.ObservationInvalidated.GetFamilies() {
				family, ok := bridge.FactFamilyFromWire(wire)
				if !ok {
					// An unknown family can be anything: an authority-sized wake.
					authority = true
					continue
				}
				if !seen[family] {
					seen[family] = true
					families = append(families, family)
				}
			}
		}
	}
	return outcomes, families, authority, stopped, stopAt
}
func wakeOutcome(o *k.OperationOutcome) WakeOutcome {
	_, unknown := o.Outcome.(*k.OperationOutcome_Unknown)
	return WakeOutcome{Action: domain.ActionID(o.Attempt.GetActionId()), Attempt: domain.AttemptID(o.Attempt.GetAttemptId()), Terminal: !unknown}
}
