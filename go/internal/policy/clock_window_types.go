package policy

import (
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type ClockWindowReview struct {
	Revision                         uint64
	Captured, Reviewed, Acknowledged int64
	HasHolds                         domain.Fact[bool]
}

type ClockWindowState string

const (
	ClockNeverStarted ClockWindowState = "never_started"
	ClockRunning      ClockWindowState = "running"
	ClockStopping     ClockWindowState = "stopping"
	ClockStopped      ClockWindowState = "stopped"
)

type ClockWindowStatus struct {
	Snapshot                                        domain.GenerationSnapshot
	Tick                                            domain.Tick
	State                                           ClockWindowState
	ActualPaused, NativeTickBoundary, DurableEvents domain.Fact[bool]
	NewestCursor                                    domain.Fact[int64]
}

type ClockWindowObligations struct {
	Complete, OwnedEpochPending, UnknownStartPending domain.Fact[bool]
}

type ClockWindowFacts struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Emergency             EmergencySnapshot
	Review                ClockWindowReview
	Status                ClockWindowStatus
	Obligations           ClockWindowObligations
	WorkRemaining         domain.Fact[bool]
	// CombatPlan is whether the ActiveCombat goal holds an admitted plan with
	// open work. Only then may live hostiles be watched instead of refused.
	CombatPlan domain.Fact[bool]
	// SquadUnanswered is whether the defense planner, at this tick, found
	// no eligible squad for the emergency's threats. A hostile building it
	// cannot answer is then watched like a distant one, so the colony keeps
	// running around it, instead of holding the clock for good (#326); a
	// hostile pawn still holds, a raid must not auto-advance. Unknown means
	// the planner has not reported and the building holds.
	SquadUnanswered domain.Fact[bool]
	// Sheltered is whether the threat's sheltering response is complete
	// (ShelterHeld): every undrafted colonist is restricted to the Safe
	// area. A hostile pawn is then watched in a combat window instead of
	// refused, so the sheltered colony waits it out (#1560).
	Sheltered domain.Fact[bool]
}

// CombatMaxTicks bounds a combat window; zero means the colony budget. A raid
// is re-planned between short windows, so the combat budget never exceeds
// MaxTicks.
type ClockWindowLimits struct {
	Now            time.Time
	MaxAge         time.Duration
	MaxTicks       uint32
	CombatMaxTicks uint32
}

type ClockWindowMode string

const (
	ClockWindowColony ClockWindowMode = "colony"
	ClockWindowCombat ClockWindowMode = "combat"
)

type ClockWindowReason string

const (
	ClockWindowStale         ClockWindowReason = "stale_facts"
	ClockWindowUnknown       ClockWindowReason = "unknown_facts"
	ClockWindowUnsafe        ClockWindowReason = "unsafe_colony"
	ClockWindowOutstanding   ClockWindowReason = "outstanding_epoch"
	ClockWindowUnreviewed    ClockWindowReason = "unreviewed_events"
	ClockWindowInterrupted   ClockWindowReason = "interruption"
	ClockWindowNotPaused     ClockWindowReason = "not_paused"
	ClockWindowNoWork        ClockWindowReason = "no_work"
	ClockWindowInvalidLimits ClockWindowReason = "invalid_limits"
)

// Hostiles lists, sorted, the live undowned threats a combat window
// acknowledges; it is empty in colony mode. Downed lists, sorted, the
// colonists already known downed at admission: the native watcher stops a
// window for any unacknowledged downed colonist, so an unacknowledged known
// casualty stopped every window at zero ticks and neither the fight nor the
// rescue could finish (#213). A colonist who goes down during the window
// still stops it.
type ClockWindowDecision struct {
	Admitted       bool
	Refused        []ClockWindowReason
	Snapshot       domain.GenerationSnapshot
	Tick           domain.Tick
	ReviewRevision uint64
	CapturedCursor int64
	MaxTicks       uint32
	Mode           ClockWindowMode
	Hostiles       []PawnID
	Downed         []PawnID
}
