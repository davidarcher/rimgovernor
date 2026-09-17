package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// EvaluateClockWindow admits a finite window. Live hostiles refuse it unless
// the ActiveCombat goal holds an admitted plan; then the window is a combat
// watch acknowledging exactly those hostiles under the combat budget, and it
// returns to colony mode once every hostile is dead or downed. The returned
// snapshot and review revision must still match at runtime dispatch.
func EvaluateClockWindow(f ClockWindowFacts, limits ClockWindowLimits) ClockWindowDecision {
	result := ClockWindowDecision{Refused: []ClockWindowReason{}}
	hold := func(reason ClockWindowReason) {
		for _, old := range result.Refused {
			if old == reason {
				return
			}
		}
		result.Refused = append(result.Refused, reason)
	}
	if limits.Now.IsZero() || limits.MaxAge <= 0 || limits.MaxTicks == 0 || limits.MaxTicks > 1800000 || limits.CombatMaxTicks > limits.MaxTicks || f.Tick >= 0 && int64(f.Tick) > math.MaxInt64-int64(limits.MaxTicks) {
		hold(ClockWindowInvalidLimits)
	}
	if f.Current.Validate() != nil || f.Current.Revision == 0 || f.Current.Native == 0 || f.Tick < 0 {
		hold(ClockWindowUnknown)
	}
	if f.Status.Snapshot != f.Current || f.Status.Tick != f.Tick {
		hold(ClockWindowStale)
	}
	// MaxAge below bounds the admission reads alone; the planner facts are
	// bound by tick instead, so a step that skipped the planners still
	// admits from facts observed at this paused tick and never from facts
	// an earlier window has since outrun. Unknown means no planner has
	// observed yet: the work then comes from the journal alone.
	if planned, known := f.FactsTick.Value(); known && planned != f.Tick {
		hold(ClockWindowStalePlanning)
	}
	if f.StartedAt.IsZero() || f.ObservedAt.IsZero() || f.ObservedAt.Before(f.StartedAt) || f.ObservedAt.After(limits.Now) || f.StartedAt.After(limits.Now) || limits.Now.Sub(f.StartedAt) > limits.MaxAge || limits.Now.Sub(f.ObservedAt) > limits.MaxAge {
		hold(ClockWindowStale)
	}
	combatPlan, combatKnown := f.CombatPlan.Value()
	var hostiles []PawnID
	emergency := EvaluateEmergency(f.Emergency, f.Current, f.Tick)
	for _, h := range emergency.Holds {
		switch h.Reason {
		case EmergencyStaleFacts:
			hold(ClockWindowStale)
		case EmergencyUnknownFacts:
			hold(ClockWindowUnknown)
		case EmergencyUnsafeThreat:
			// An admitted combat plan needs ticks for defenders to fight; the
			// native watcher only lets them pass with every live hostile
			// acknowledged. Without a plan a raid must not auto-advance.
			switch {
			case !combatKnown:
				hold(ClockWindowUnknown)
			case combatPlan && h.Pawn != "":
				hostiles = append(hostiles, h.Pawn)
			default:
				hold(ClockWindowUnsafe)
			}
		case EmergencyCriticalMedical:
			// Must NOT refuse the window here: RoutineTendPlanner dispatches the
			// tend order regardless of window admission, but the native side can
			// only carry it out -- and NeedsTend can only clear -- while ticks are
			// actually passing. Refusing to admit a window while NeedsTend is true
			// deadlocks: the order times out unexecuted, the reviewer retries next
			// poll, and the colony is unsafe forever. Unlike a hostile threat, an
			// untended patient is resolved BY letting the clock run, not by holding it.
		default:
			hold(ClockWindowUnsafe)
		}
	}
	check := func(fact domain.Fact[bool], want bool, reason ClockWindowReason) {
		value, known := fact.Value()
		if !known {
			hold(ClockWindowUnknown)
		} else if value != want {
			hold(reason)
		}
	}
	check(f.Obligations.Complete, true, ClockWindowUnknown)
	check(f.Obligations.OwnedEpochPending, false, ClockWindowOutstanding)
	check(f.Obligations.UnknownStartPending, false, ClockWindowOutstanding)
	check(f.WorkRemaining, true, ClockWindowNoWork)
	switch f.Status.State {
	case ClockNeverStarted, ClockStopped:
	case ClockRunning, ClockStopping:
		hold(ClockWindowNotPaused)
	default:
		hold(ClockWindowUnknown)
	}
	check(f.Status.ActualPaused, true, ClockWindowNotPaused)
	check(f.Status.NativeTickBoundary, true, ClockWindowUnknown)
	durable, known := f.Status.DurableEvents.Value()
	if !known || !durable && f.Status.State != ClockNeverStarted {
		hold(ClockWindowUnknown)
	}
	review := f.Review
	if review.Captured < 0 || review.Reviewed < 0 || review.Acknowledged < 0 || review.Acknowledged > review.Reviewed || review.Reviewed != review.Captured {
		hold(ClockWindowUnreviewed)
	}
	newest, known := f.Status.NewestCursor.Value()
	if !known {
		hold(ClockWindowUnknown)
	} else if newest < 0 || newest != review.Captured {
		hold(ClockWindowUnreviewed)
	}
	check(review.HasHolds, false, ClockWindowInterrupted)
	if len(result.Refused) != 0 {
		return result
	}
	result.Admitted = true
	result.Snapshot = f.Current
	result.Tick = f.Tick
	result.ReviewRevision = review.Revision
	result.CapturedCursor = review.Captured
	result.MaxTicks = limits.MaxTicks
	result.Mode = ClockWindowColony
	if len(hostiles) > 0 {
		sort.Slice(hostiles, func(i, j int) bool { return hostiles[i] < hostiles[j] })
		result.Mode = ClockWindowCombat
		result.Hostiles = hostiles
		if limits.CombatMaxTicks != 0 {
			result.MaxTicks = limits.CombatMaxTicks
		}
	}
	return result
}
