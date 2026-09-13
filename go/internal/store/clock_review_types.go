package store

import "github.com/davidarcher/RimGovernor/go/internal/store/clock"

type ClockHoldKind = clock.HoldKind

const (
	ClockInterruptionHold = clock.InterruptionHold
	ClockGapHold          = clock.GapHold
)

type ClockHold = clock.Hold
type ClockReviewState = clock.ReviewState
type ClockAcknowledgement = clock.Acknowledgement
