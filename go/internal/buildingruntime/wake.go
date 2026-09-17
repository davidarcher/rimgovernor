package buildingruntime

import (
	"sync"

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
	authority bool
}

func NewWakeSignal() *WakeSignal {
	return &WakeSignal{ch: make(chan struct{}, 1), pending: map[domain.ActionID]WakeOutcome{}}
}

// Notify merges outcomes into the pending set and signals without blocking.
func (w *WakeSignal) Notify(outcomes []WakeOutcome, authority bool) {
	if w == nil {
		return
	}
	w.mu.Lock()
	for _, o := range outcomes {
		w.pending[o.Action] = o
	}
	w.authority = w.authority || authority
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

// Take drains the pending outcomes and the authority flag.
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

// clockPageWake reads the wake-relevant facts out of one committed page.
func clockPageWake(page *k.EventsPage) (outcomes []WakeOutcome, authority bool) {
	for _, event := range page.GetEvents() {
		switch v := event.Event.(type) {
		case *k.Event_OperationOutcome:
			outcomes = append(outcomes, wakeOutcome(v.OperationOutcome))
		case *k.Event_Stopped:
			if watch := v.Stopped.GetWatch(); watch != nil {
				outcomes = append(outcomes, wakeOutcome(watch.Outcome))
			}
		case *k.Event_AuthorityChanged:
			authority = true
		}
	}
	return outcomes, authority
}
func wakeOutcome(o *k.OperationOutcome) WakeOutcome {
	_, unknown := o.Outcome.(*k.OperationOutcome_Unknown)
	return WakeOutcome{Action: domain.ActionID(o.Attempt.GetActionId()), Attempt: domain.AttemptID(o.Attempt.GetAttemptId()), Terminal: !unknown}
}
