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
	// stopAt is the native stamp of the earliest stop still pending for
	// the step loop, zero when the page carried none; the step that acts
	// on it publishes the stop-to-step latency (issue #112).
	stopAt time.Time
	// stops counts committed stops; stopsTaken is the count the step loop
	// last drained, so a step reason reports whether a stop is pending.
	// No admission waits for a stop: every kind the Worker dispatches is
	// validated natively at apply time and dispatches under a running
	// window as readily as between windows (#244).
	stops, stopsTaken uint64
}

func NewWakeSignal() *WakeSignal {
	return &WakeSignal{ch: make(chan struct{}, 1), pending: map[domain.ActionID]WakeOutcome{}, families: map[bridge.FactFamily]bool{}}
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
		w.stops++
		if w.stopAt.IsZero() || !stopAt.IsZero() && stopAt.Before(w.stopAt) {
			w.stopAt = stopAt
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

// TakeInvalidated drains the pending outcomes, invalidated families, the
// authority flag and the pending stop into one step reason.
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
