package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"time"
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
	Current domain.GenerationSnapshot
	Tick    domain.Tick
	// FactsTick is the tick the planner facts behind WorkRemaining were
	// observed at; unknown when no planner has run yet (the work is then
	// the journal's alone). Known facts from another tick hold the window.
	FactsTick             domain.Fact[domain.Tick]
	StartedAt, ObservedAt time.Time
	Emergency             EmergencySnapshot
	Review                ClockWindowReview
	Status                ClockWindowStatus
	Obligations           ClockWindowObligations
	WorkRemaining         domain.Fact[bool]
	// CombatPlan is whether the ActiveCombat goal holds an admitted plan with
	// open work. Only then may live hostiles be watched instead of refused.
	CombatPlan domain.Fact[bool]
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
	// ClockWindowStalePlanning: the planner facts predate the admitted tick.
	ClockWindowStalePlanning ClockWindowReason = "stale_planning"
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
