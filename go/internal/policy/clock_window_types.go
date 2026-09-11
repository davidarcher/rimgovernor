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
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Emergency             EmergencySnapshot
	Review                ClockWindowReview
	Status                ClockWindowStatus
	Obligations           ClockWindowObligations
	WorkRemaining         domain.Fact[bool]
}

type ClockWindowLimits struct {
	Now      time.Time
	MaxAge   time.Duration
	MaxTicks uint32
}
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

type ClockWindowDecision struct {
	Admitted       bool
	Refused        []ClockWindowReason
	Snapshot       domain.GenerationSnapshot
	Tick           domain.Tick
	ReviewRevision uint64
	CapturedCursor int64
	MaxTicks       uint32
}
