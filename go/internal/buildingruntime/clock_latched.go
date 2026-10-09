package buildingruntime

import (
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// clockLatchedOutcome is one terminal outcome the native clock latched for
// an attempt the journal still shows in flight. The scheduler defers
// admission while the Worker reconciles it: the stop that
// carried the outcome wakes the Worker and the step loop alike, and a
// review that wins the player gate first would otherwise watch the settled
// attempt again while its successor is still undispatched.
type clockLatchedOutcome struct {
	Attempt domain.AttemptID
	// Holds counts the steps deferred on this outcome; past
	// clockLatchedHoldMax the review proceeds so a Worker that cannot
	// reconcile the attempt never parks the clock.
	Holds int
	// Escaped marks an outcome whose forced pass-through was already taken.
	Escaped bool
}

const clockLatchedHoldMax = 3

// clockLatched is the set of latched outcomes the Worker has yet to
// reconcile. The poll loop records them as it commits a page and the step
// loop's reason repeats them; a step consults the set under the player
// gate, so the poll's own write is the one that needs the lock. It also
// counts the holds spent on undispatched work (see undispatched).
type clockLatched struct {
	mu       sync.Mutex
	outcomes map[domain.ActionID]clockLatchedOutcome
	waiting  map[domain.ActionID]int
	// deferrals counts the steps deferred in a row, over both holds and
	// every action: past clockLatchedHoldMax the review proceeds however
	// many actions still owe, so a plan of several undispatchable actions
	// (hauls the game keeps refusing) cannot park the clock for the sum
	// of their holds. A step that is not deferred resets it (released).
	deferrals int
	// queued is every action, of any kind, that the last undispatched
	// call saw pending or prepared. One of them found at a later stage
	// is the Worker's progress: the holds restart, so a plan the Worker
	// is dispatching step by step keeps the clock stopped until its last
	// action is out instead of losing the window after three steps spent
	// on other plans' work (a 32-wall shell went out over ~19,000 ticks
	// of a running window). An action that vanished (a replaced plan) is
	// not progress, so planner churn cannot park the clock.
	queued map[domain.ActionID]bool
	// escaped is the queued actions whose forced pass-through was already
	// reported, forgotten when the action leaves those stages; escapes is
	// the passes not yet taken by the scheduler.
	escaped map[domain.ActionID]bool
	escapes []domain.ActionID
}

func newClockLatched() *clockLatched {
	return &clockLatched{outcomes: map[domain.ActionID]clockLatchedOutcome{}, waiting: map[domain.ActionID]int{}, queued: map[domain.ActionID]bool{}, escaped: map[domain.ActionID]bool{}}
}

// escape queues, once per action, the queued actions a bound just let
// through to review; the caller holds the lock.
func (l *clockLatched) escape(seen map[domain.ActionID]bool) {
	for id := range seen {
		if !l.escaped[id] {
			l.escaped[id] = true
			l.escapes = append(l.escapes, id)
		}
	}
}

// takeEscapes returns, in order, the actions whose hold bound forced the
// review through since the last call.
func (l *clockLatched) takeEscapes() []domain.ActionID {
	l.mu.Lock()
	defer l.mu.Unlock()
	out := l.escapes
	l.escapes = nil
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// progressed records the queued set and reports whether an action the
// previous call saw queued has since moved on.
func (l *clockLatched) progressed(items []clockWorkItem) bool {
	moved := false
	queued := map[domain.ActionID]bool{}
	for _, item := range items {
		if item.Stage == domain.Pending || item.Stage == domain.Prepared {
			queued[item.Action] = true
		} else if l.queued[item.Action] {
			moved = true
		}
	}
	l.queued = queued
	return moved
}

// undispatched reports, in order, the watched-kind actions still pending
// or prepared while none of that kind is dispatched, counting one hold on
// each: the successor a planner just queued (or the Worker just unblocked)
// has yet to be dispatched, and a window admitted first would run out its
// whole budget watching nothing. Each action is held at most
// clockLatchedHoldMax steps over its life, so work the Worker cannot
// dispatch (materials, a blocked cell) never parks the clock; the count
// is forgotten once the action leaves those stages.
func (l *clockLatched) undispatched(items []clockWorkItem) []domain.ActionID {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.progressed(items) {
		l.deferrals = 0
		clear(l.waiting)
		clear(l.escaped)
	}
	var waiting []domain.ActionID
	seen := map[domain.ActionID]bool{}
	dispatched := false
	for _, item := range items {
		if !clockWatchedKind(item.Kind) {
			continue
		}
		switch item.Stage {
		case domain.Dispatched, domain.AwaitingObservation:
			dispatched = true
		case domain.Pending, domain.Prepared:
			seen[item.Action] = true
			if l.waiting[item.Action] < clockLatchedHoldMax {
				waiting = append(waiting, item.Action)
			}
		}
	}
	for id := range l.waiting {
		if !seen[id] {
			delete(l.waiting, id)
		}
	}
	for id := range l.escaped {
		if !seen[id] {
			delete(l.escaped, id)
		}
	}
	if !dispatched && len(seen) > 0 && (len(waiting) == 0 || l.deferrals >= clockLatchedHoldMax) {
		l.escape(seen)
	}
	if dispatched || len(waiting) == 0 || l.deferrals >= clockLatchedHoldMax {
		return nil
	}
	l.deferrals++
	for _, id := range waiting {
		l.waiting[id]++
	}
	sort.Slice(waiting, func(i, j int) bool { return waiting[i] < waiting[j] })
	return waiting
}

// remember records the terminal outcomes a wake carried.
func (l *clockLatched) remember(events []WakeOutcome) {
	l.mu.Lock()
	defer l.mu.Unlock()
	for _, event := range events {
		if event.Terminal && event.Attempt != 0 {
			l.outcomes[event.Action] = clockLatchedOutcome{Attempt: event.Attempt}
		}
	}
}

// pending drops the latched outcomes the plan has absorbed (the attempt is
// terminal, superseded or gone) and reports, in order, those a watched
// attempt still owes, counting one hold on each. It reports none once an
// outcome has been held its bound, and none while any other watched
// attempt is in flight: that one is fresh to the native clock, so a window
// admitted now has an outcome to latch, and the settled attempt reconciles
// under it.
func (l *clockLatched) pending(items []clockWorkItem) []domain.ActionID {
	l.mu.Lock()
	defer l.mu.Unlock()
	if len(l.outcomes) == 0 {
		return nil
	}
	inFlight := map[domain.ActionID]bool{}
	fresh := false
	for _, item := range items {
		if !clockWatchedKind(item.Kind) || (item.Stage != domain.Dispatched && item.Stage != domain.AwaitingObservation) {
			continue
		}
		if latched, ok := l.outcomes[item.Action]; ok && item.Attempt == latched.Attempt {
			inFlight[item.Action] = true
		} else {
			fresh = true
		}
	}
	var pending []domain.ActionID
	for id, latched := range l.outcomes {
		if inFlight[id] && !fresh && latched.Holds >= clockLatchedHoldMax {
			l.escapes = append(l.escapes, id)
		}
		if !inFlight[id] || latched.Holds >= clockLatchedHoldMax {
			delete(l.outcomes, id)
			continue
		}
		if fresh || l.deferrals >= clockLatchedHoldMax {
			if !fresh && !latched.Escaped {
				latched.Escaped = true
				l.outcomes[id] = latched
				l.escapes = append(l.escapes, id)
			}
			continue
		}
		latched.Holds++
		l.outcomes[id] = latched
		pending = append(pending, id)
	}
	if len(pending) > 0 {
		l.deferrals++
	}
	sort.Slice(pending, func(i, j int) bool { return pending[i] < pending[j] })
	return pending
}

// released records a step that was not deferred, ending the run of
// deferrals the bound counts.
func (l *clockLatched) released() {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.deferrals = 0
}
